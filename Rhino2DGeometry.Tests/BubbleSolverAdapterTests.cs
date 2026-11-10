using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using BubbleSolver;
using ExcelToBubbleDiagram;
using Rhino;
using Rhino.Geometry;
using SeniorDesign.Rhino2DGeometry;

internal static class BubbleSolverAdapterTests
{
    public static string RunManaged()
    {
        Check(!typeof(RoomInput).Assembly.GetReferencedAssemblies().Any(assembly =>
            assembly.Name.Contains("Rhino") || assembly.Name.Contains("Kangaroo")),
            "The imported solver must remain independent of CAD libraries.");
        Check(BubbleSolverAdapter.DepartmentToken("7.10") == 7 &&
              BubbleSolverAdapter.DepartmentToken("7.20.1") == 7 &&
              BubbleSolverAdapter.DepartmentToken("Unassigned") == -1,
              "Department mapping must follow the original reader's leading numeric token.");

        var fixture = Fixture();
        var options = new BubbleDiagramOptions();
        var first = BubbleSolverAdapter.Solve(fixture, options);
        var second = BubbleSolverAdapter.Solve(fixture, options);
        Check(first.Converged && first.FloorEnvelopes.Count == 2, "Text floor labels must map to two solver floors.");
        Check(first.Rooms.Zip(second.Rooms).All(pair =>
            pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y), "Repeated solves must be deterministic.");
        Check(first.Rooms.All(room => room.Dept == 7), "Rooms in department 7 must share a solver department.");
        Check(BubbleLayoutEngine.UsesComponent(options, null), "Automatic circles must use BubbleSolver.");
        Check(!BubbleLayoutEngine.UsesComponent(new BubbleDiagramOptions { Shape = BubbleShape.Square }, null),
            "Squares must retain their existing layout path.");

        fixture.Nodes[0].Source.NetSquareFeet = double.NaN;
        ExpectFailure(() => BubbleSolverAdapter.Solve(fixture, options), "Invalid areas must be rejected.");
        fixture = Fixture();
        fixture.Nodes[1].Index = fixture.Nodes[0].Index;
        ExpectFailure(() => BubbleSolverAdapter.Solve(fixture, options), "Duplicate indices must be rejected.");
        fixture = Fixture();
        fixture.Nodes[1].Source.Department = fixture.Nodes[0].Source.Department;
        ExpectFailure(() => BubbleSolverAdapter.Solve(fixture, options), "Duplicate UIDs must be rejected.");
        fixture = Fixture();
        fixture.Connections.Add(new ConnectionPair(0, 999));
        ExpectFailure(() => BubbleSolverAdapter.Solve(fixture, options), "Missing connection targets must be rejected.");
        fixture = Fixture();
        fixture.Connections.Add(new ConnectionPair(0, 2));
        ExpectFailure(() => BubbleSolverAdapter.Solve(fixture, options), "Cross-floor connections must be rejected.");
        ExpectFailure(() => BubbleSolverAdapter.Solve(Fixture(), new BubbleDiagramOptions { SolverPaddingFeet = double.NaN }),
            "Nonfinite configuration must be rejected.");

        foreach (var workbook in Workbooks())
        {
            var data = BubbleDiagramExcelReader.Load(workbook, "ProgramInfo", 1);
            var result = BubbleSolverAdapter.Solve(data, options);
            Check(result.Rooms.Count == 151 && result.FloorEnvelopes.Count == data.FloorCount,
                "Workbook room and floor counts must survive the adapter.");
            var areas = data.Nodes.ToDictionary(node => node.Uid, node => node.Source.NetSquareFeet);
            Check(result.Rooms.All(room => Math.Abs(Math.PI * Math.Pow(room.Radius / 0.3048, 2) - areas[room.Uid]) < 1e-7),
                "Solver radii must preserve workbook square-foot areas.");
            Console.WriteLine(Path.GetFileName(workbook) + ": BubbleSolver converged for 151 rooms on " + data.FloorCount + " floors.");
            ExpectFailure(() => BubbleSolverAdapter.Solve(data, new BubbleDiagramOptions { DeoverlapIterations = 1 }),
                "A capped cleanup must stop before baking geometry.");
        }
        return "BubbleSolver adapter tests passed (component isolation, mapping, validation, convergence, and three workbooks).";
    }

    public static string RunRhino(RhinoDoc doc)
    {
        foreach (var units in new[] { UnitSystem.Feet, UnitSystem.Meters, UnitSystem.Millimeters })
        {
            var scale = RhinoMath.UnitScale(UnitSystem.Feet, units);
            foreach (var workbook in Workbooks())
            {
                using var scenarioDoc = RhinoDoc.CreateHeadless(null);
                scenarioDoc.ModelUnitSystem = units;
                scenarioDoc.ModelAbsoluteTolerance = 0.001 * scale;
                var data = BubbleDiagramExcelReader.Load(workbook, "ProgramInfo", scale);
                var options = new BubbleDiagramOptions();
                var layouts = BubbleLayoutEngine.Solve(data, options, null, scenarioDoc.ModelAbsoluteTolerance, scale);
                try
                {
                    ValidateLayouts(layouts, options, scale);
                    BubbleDiagramWriter.AddToDocument(scenarioDoc, layouts, options);
                    var rooms = BubbleMetadataReader.Read(scenarioDoc.Objects.ToList(), 3, out var ignored);
                    Check(rooms.Count == 151 && ignored > 0, "The writer and reader must preserve all rooms among diagram objects.");
                    var source = data.Nodes.ToDictionary(node => node.Uid);
                    foreach (var room in rooms)
                    {
                        var node = source[room.Uid];
                        var numericFloor = int.TryParse(node.Source.Floor, out var floor) ? floor : 0;
                        Check(room.Area == node.Source.NetSquareFeet && room.Label == node.DisplayName &&
                              room.Department == node.Source.Department && room.Color.ToArgb() == node.Source.Color.ToArgb() &&
                              room.FloorPreference == numericFloor,
                              "Room metadata and colors must survive the Rhino handoff.");
                    }
                    var settings = new GenerationSettings { UnitScale = scale };
                    var plan = FloorplanPipeline.Generate(rooms, settings, scenarioDoc.ModelAbsoluteTolerance);
                    Check(plan.Rooms.Count == 151, "The floorplan pipeline must accept every solver room.");
                    if (Path.GetFileNameWithoutExtension(workbook).EndsWith("_2"))
                        Check(DocumentWriter.Write(scenarioDoc, plan, settings) > 0, "The two-floor workbook must bake a floorplan.");
                    Console.WriteLine(Path.GetFileName(workbook) + " / " + units + ": 151 circles, metadata, clearance, and floorplan generation passed.");
                }
                finally
                {
                    foreach (var layout in layouts) layout.Boundary.Dispose();
                }
            }
        }

        doc.ModelUnitSystem = UnitSystem.Feet;
        doc.ModelAbsoluteTolerance = 0.001;
        using (var boundary = new Rectangle3d(Plane.WorldXY, new Interval(-50, 50), new Interval(-50, 50)).ToNurbsCurve())
        {
            Check(!BubbleLayoutEngine.UsesComponent(new BubbleDiagramOptions(), boundary),
                "Selecting a boundary must retain the constrained layout path.");
            foreach (var shape in new[] { BubbleShape.Circle, BubbleShape.Square })
            {
                var layouts = BubbleLayoutEngine.Solve(Fixture(), new BubbleDiagramOptions { Shape = shape }, boundary, 0.001);
                Check(layouts.Count == 2 && layouts.SelectMany(layout => layout.Nodes).All(node => node.Position.IsValid),
                    "Constrained circles and squares must still produce valid layouts.");
                foreach (var layout in layouts) layout.Boundary.Dispose();
            }
        }
        return "BubbleSolver Rhino tests passed (feet/meters/millimeters, circle bake, floorplan handoff, and legacy layouts).";
    }

    private static void ValidateLayouts(IReadOnlyList<FloorLayout> layouts, BubbleDiagramOptions options, double scale)
    {
        var previousMaxY = double.NegativeInfinity;
        foreach (var layout in layouts)
        {
            var bounds = layout.Boundary.GetBoundingBox(true);
            Check(bounds.Min.Y + layout.DocumentOffset.Y > previousMaxY, "Floor diagrams must be spaced apart.");
            previousMaxY = bounds.Max.Y + layout.DocumentOffset.Y;
            for (var first = 0; first < layout.Nodes.Count; first++)
            {
                var a = layout.Nodes[first];
                Check(a.Position.IsValid && Math.Abs(Math.PI * Math.Pow(a.Radius / scale, 2) - a.Source.NetSquareFeet) < 1e-7,
                    "Document units must preserve circle areas.");
                Check(bounds.Contains(a.Position - new Vector3d(a.Radius, a.Radius, 0)) &&
                      bounds.Contains(a.Position + new Vector3d(a.Radius, a.Radius, 0)),
                      "The automatic frame must enclose the entire circles.");
                for (var second = first + 1; second < layout.Nodes.Count; second++)
                {
                    var b = layout.Nodes[second];
                    Check(a.Position.DistanceTo(b.Position) + 0.01000001 * scale >=
                          a.Radius + b.Radius + options.SolverPaddingFeet * scale,
                          "Document geometry must satisfy the requested physical gap.");
                }
            }
        }
    }

    private static BubbleDiagramData Fixture()
    {
        var data = new BubbleDiagramData();
        for (var index = 0; index < 4; index++)
        {
            var area = 100 + index * 50;
            data.Nodes.Add(new BubbleNode
            {
                Index = index,
                Source = new ProgramRow
                {
                    Department = "7." + (index + 1), ProgramElement = "Room " + index,
                    Floor = index < 2 ? "2" : "Roof", Quantity = 1, NetSquareFeet = area,
                    Color = Color.CornflowerBlue
                },
                Radius = Math.Sqrt(area / Math.PI), SquareSide = Math.Sqrt(area)
            });
        }
        data.Connections.Add(new ConnectionPair(0, 1));
        data.Connections.Add(new ConnectionPair(2, 3));
        return data;
    }

    private static IEnumerable<string> Workbooks()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Program_ExcelToGrasshopper_Test.xlsx")))
            root = root.Parent;
        Check(root != null, "Could not locate workbook fixtures.");
        return new[] { "Program_ExcelToGrasshopper_Test.xlsx", "Program_ExcelToGrasshopper_Test.xlsm", "Program_ExcelToGrasshopper_Test_2.xlsm" }
            .Select(name => Path.Combine(root.FullName, name));
    }

    private static void ExpectFailure(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
