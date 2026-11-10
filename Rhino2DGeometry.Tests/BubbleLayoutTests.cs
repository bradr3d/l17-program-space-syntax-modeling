using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ExcelToBubbleDiagram;
using KangarooSolver;
using Particle = KangarooSolver.Particle;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

internal static class BubbleLayoutTests
{
    public static string RunCollision()
    {
        var points = Enumerable.Range(0, 40).Select(index => new Point3d(index, 0, 0)).ToList();
        var radii = points.Select(point => 50.0).ToList();
        var goal = new VariableRadiusCollisionGoal(points, radii, 0.25, 10);
        var system = new PhysicalSystem();
        system.AssignPIndex(goal, 0.001);
        goal.Calculate(system.Particles.ToList());
        var maximumMove = goal.Move.Max(move => move.Length);
        if (maximumMove > 50.125 + 1e-9)
            throw new Exception("Collision corrections grow with neighbor count: maximum move = " + maximumMove);
        var coincident = new VariableRadiusCollisionGoal(
            new[] { Point3d.Origin, Point3d.Origin }, new[] { 50.0, 50.0 }, 0.25, 10);
        coincident.PIndex = new[] { 0, 1 };
        coincident.Calculate(new List<Particle> { new Particle(Point3d.Origin, 1), new Particle(Point3d.Origin, 1) });
        if (Math.Abs(coincident.Move[0].Length - 50.125) > 1e-9 || coincident.Move[0] != -coincident.Move[1])
            throw new Exception("Coincident centers must receive equal and opposite full separation corrections.");
        var separated = new List<Particle> { new Particle(Point3d.Origin, 1), new Particle(new Point3d(200, 0, 0), 1) };
        coincident.Calculate(separated);
        if (coincident.Move.Any(move => move.Length != 0) || coincident.Weighting.Any(weight => weight != 0))
            throw new Exception("Collision state must reset once bubbles separate.");
        return "Bubble collision tests passed (dense cluster corrections remain bounded).";
    }

    public static string Run(RhinoDoc doc)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Program_ExcelToGrasshopper_Test.xlsx")))
            root = root.Parent;
        var data = BubbleDiagramExcelReader.Load(Path.Combine(root.FullName, "Program_ExcelToGrasshopper_Test.xlsx"), "ProgramInfo", 1);
        var options = new BubbleDiagramOptions();
        var layouts = BubbleLayoutEngine.Solve(data, options, null, 0.001);
        foreach (var layout in layouts)
        {
            var bounds = layout.Boundary.GetBoundingBox(true);
            var limit = bounds.Diagonal.Length * 2;
            var maxDistance = layout.Nodes.Max(node => node.Position.DistanceTo(bounds.Center));
            Console.WriteLine("Bubble layout maximum center distance: " + maxDistance);
            if (layout.Nodes.Any(node => !node.Position.IsValid) || !double.IsFinite(maxDistance) || maxDistance > limit)
                throw new Exception("Bubble solver escaped the floor boundary at default settings.");
        }
        BubbleDiagramWriter.AddToDocument(doc, layouts, options);
        var bubbles = doc.Objects.Where(obj => obj.Attributes.GetUserString("Area") != null).ToList();
        if (bubbles.Count != 151 || bubbles.Any(obj => !(obj.Geometry is Curve curve) || !curve.TryGetCircle(out var circle) || circle.Radius <= 0))
            throw new Exception("Expected 151 valid baked circle curves.");
        foreach (var obj in doc.Objects.ToList()) doc.Objects.Delete(obj.Id, true);
        return "Bubble layout and circle bake tests passed for 151 rooms.";
    }
}
