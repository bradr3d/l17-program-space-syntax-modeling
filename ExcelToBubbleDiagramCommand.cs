// Rhino 8 .NET plug-in source for the ExcelToBubbleDiagram Grasshopper definition.
//
// Include this file in a RhinoCommon plug-in (net7.0-windows or net8.0-windows) and add:
//   1. RhinoCommon
//      C:\Program Files\Rhino 8\System\netcore\RhinoCommon.dll
//      Copy Local = false
//   2. KangarooSolver
//      C:\Program Files\Rhino 8\Plug-ins\Grasshopper\Components\KangarooSolver.dll
//      Copy Local = false. Grasshopper must be installed; the plug-in should
//      resolve KangarooSolver.dll / Grasshopper.dll from the Rhino install.
//   3. ClosedXML (NuGet)
//      <PackageReference Include="ClosedXML" Version="0.105.0" />
//      Copy this package (and its dependencies) next to the .rhp.
//
// Command: ExcelToBubbleDiagram
// Reads ProgramInfo, expands room quantities, sizes bubbles from net SF, relaxes
// each floor with Kangaroo2, and bakes colored curves, connections, and labels.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using KangarooSolver;
using KangarooSolver.Goals;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace ExcelToBubbleDiagram
{
    public enum BubbleShape
    {
        Circle,
        Square
    }

    public sealed class BubbleDiagramOptions
    {
        public string WorksheetName { get; set; } = "ProgramInfo";
        public BubbleShape Shape { get; set; } = BubbleShape.Circle;
        public double BoundaryScale { get; set; } = 1.5;
        public double Damping { get; set; } = 0.99;
        public int Iterations { get; set; } = 500;
        public double CollisionStrength { get; set; } = 10.0;
        public double ConnectionStrength { get; set; } = 0.05;
        public double BoundaryStrength { get; set; } = 10.0;
        public double BubbleGap { get; set; } = 0.25;
    }

    internal sealed class ProgramRow
    {
        public int ExcelRow { get; set; }
        public string Department { get; set; } = string.Empty;
        public string ProgramElement { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public double NetSquareFeet { get; set; }
        public string Connections { get; set; } = string.Empty;
        public Color Color { get; set; }
        public string Floor { get; set; } = string.Empty;
    }

    internal sealed class BubbleNode
    {
        public int Index { get; set; }
        public ProgramRow Source { get; set; } = null!;
        public int CopyNumber { get; set; }
        public double Radius { get; set; }
        public double SquareSide { get; set; }
        public Point3d Position { get; set; }

        public string DisplayName =>
            Source.Quantity > 1
                ? $"{Source.ProgramElement} {CopyNumber + 1}"
                : Source.ProgramElement;

        public string Uid
        {
            get
            {
                var department = string.IsNullOrWhiteSpace(Source.Department)
                    ? $"R{Source.ExcelRow}"
                    : Source.Department.Trim();
                return $"{department}#{CopyNumber}";
            }
        }
    }

    internal readonly struct ConnectionPair : IEquatable<ConnectionPair>
    {
        public ConnectionPair(int first, int second)
        {
            A = Math.Min(first, second);
            B = Math.Max(first, second);
        }

        public int A { get; }
        public int B { get; }

        public bool Equals(ConnectionPair other) => A == other.A && B == other.B;
        public override bool Equals(object obj) => obj is ConnectionPair other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(A, B);
    }

    internal sealed class FloorTotal
    {
        public string Floor { get; set; } = string.Empty;
        public int RoomCount { get; set; }
        public double SquareFeet { get; set; }
    }

    internal sealed class BubbleDiagramData
    {
        public List<BubbleNode> Nodes { get; } = new();
        public HashSet<ConnectionPair> Connections { get; } = new();
        public List<string> Warnings { get; } = new();
        public double TotalSquareFeet => Nodes.Sum(node => node.Source.NetSquareFeet);
        public int FloorCount => Nodes.Select(node => node.Source.Floor)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        public IReadOnlyList<FloorTotal> FloorTotals() =>
            Nodes
                .GroupBy(node => node.Source.Floor, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => double.TryParse(group.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : double.MaxValue)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new FloorTotal
                {
                    Floor = group.Key,
                    RoomCount = group.Count(),
                    SquareFeet = group.Sum(node => node.Source.NetSquareFeet)
                })
                .ToList();
    }

    internal sealed class FloorLayout
    {
        public string Floor { get; set; } = string.Empty;
        public List<BubbleNode> Nodes { get; } = new();
        public HashSet<ConnectionPair> Connections { get; } = new();
        public Curve Boundary { get; set; } = null!;
        public Vector3d DocumentOffset { get; set; }
    }

    internal static class BubbleDiagramExcelReader
    {
        private static readonly string[] RequiredHeaders =
        {
            "Dept No",
            "Program Element",
            "Room Qty/ Units",
            "Net SF/ Units",
            "Conn. Primary",
            "Color RGB",
            "Flr"
        };

        private static readonly Dictionary<string, string> HeaderAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [NormalizeHeader("Dept No")] = "Dept No",
                [NormalizeHeader("Department No")] = "Dept No",
                [NormalizeHeader("Program Element")] = "Program Element",
                [NormalizeHeader("Room Qty/ Units")] = "Room Qty/ Units",
                [NormalizeHeader("Room Quantity")] = "Room Qty/ Units",
                [NormalizeHeader("Net SF/ Units")] = "Net SF/ Units",
                [NormalizeHeader("Net SF")] = "Net SF/ Units",
                [NormalizeHeader("Conn. Primary")] = "Conn. Primary",
                [NormalizeHeader("Primary Connection")] = "Conn. Primary",
                [NormalizeHeader("Color RGB")] = "Color RGB",
                [NormalizeHeader("Colour RGB")] = "Color RGB",
                [NormalizeHeader("Flr")] = "Flr",
                [NormalizeHeader("Floor #")] = "Flr",
                [NormalizeHeader("Floor")] = "Flr"
            };

        public static BubbleDiagramData Load(string workbookPath, string worksheetName, double feetToModel)
        {
            if (!File.Exists(workbookPath))
                throw new FileNotFoundException("The Excel workbook was not found.", workbookPath);

            using var workbook = new XLWorkbook(workbookPath);
            if (!workbook.Worksheets.TryGetWorksheet(worksheetName, out var worksheet))
                throw new InvalidDataException(
                    $"Worksheet '{worksheetName}' was not found. Available sheets: " +
                    string.Join(", ", workbook.Worksheets.Select(sheet => sheet.Name)));

            var used = worksheet.RangeUsed();
            if (used == null)
                throw new InvalidDataException($"Worksheet '{worksheetName}' is empty.");

            var (headerRow, columns) = FindHeaders(worksheet, used);
            var rows = new List<ProgramRow>();

            for (var rowNumber = headerRow + 1; rowNumber <= used.RangeAddress.LastAddress.RowNumber; rowNumber++)
            {
                var programName = CellText(worksheet.Cell(rowNumber, columns["Program Element"]));
                var department = CellText(worksheet.Cell(rowNumber, columns["Dept No"]));
                if (string.IsNullOrWhiteSpace(programName) && string.IsNullOrWhiteSpace(department))
                    continue;

                var quantityText = CellText(worksheet.Cell(rowNumber, columns["Room Qty/ Units"]));
                var areaText = CellText(worksheet.Cell(rowNumber, columns["Net SF/ Units"]));
                if (!TryParseNumber(quantityText, out var quantityNumber) || quantityNumber <= 0)
                    throw new InvalidDataException(
                        $"Row {rowNumber}: 'Room Qty/ Units' must be a positive number (received '{quantityText}').");
                if (!TryParseNumber(areaText, out var area) || area <= 0)
                    throw new InvalidDataException(
                        $"Row {rowNumber}: 'Net SF/ Units' must be a positive number (received '{areaText}').");

                var quantity = Math.Max(1, (int)Math.Round(quantityNumber, MidpointRounding.AwayFromZero));
                var colorText = CellText(worksheet.Cell(rowNumber, columns["Color RGB"]));
                var floor = CellText(worksheet.Cell(rowNumber, columns["Flr"]));

                rows.Add(new ProgramRow
                {
                    ExcelRow = rowNumber,
                    Department = department,
                    ProgramElement = string.IsNullOrWhiteSpace(programName) ? $"Room {rowNumber}" : programName,
                    Quantity = quantity,
                    NetSquareFeet = area,
                    Connections = CellText(worksheet.Cell(rowNumber, columns["Conn. Primary"])),
                    Color = ParseColor(colorText, department),
                    Floor = string.IsNullOrWhiteSpace(floor) ? "1" : floor
                });
            }

            if (rows.Count == 0)
                throw new InvalidDataException("No program rows were found below the header row.");

            return BuildData(rows, feetToModel);
        }

        private static BubbleDiagramData BuildData(IReadOnlyList<ProgramRow> rows, double feetToModel)
        {
            var data = new BubbleDiagramData();
            var nodesByRow = new Dictionary<int, List<BubbleNode>>();

            foreach (var row in rows)
            {
                var rowNodes = new List<BubbleNode>();
                var modelArea = row.NetSquareFeet * feetToModel * feetToModel;
                var radius = Math.Sqrt(modelArea / Math.PI);
                var squareSide = Math.Sqrt(modelArea);

                for (var copy = 0; copy < row.Quantity; copy++)
                {
                    var node = new BubbleNode
                    {
                        Index = data.Nodes.Count,
                        Source = row,
                        CopyNumber = copy,
                        Radius = radius,
                        SquareSide = squareSide
                    };
                    data.Nodes.Add(node);
                    rowNodes.Add(node);
                }
                nodesByRow[row.ExcelRow] = rowNodes;
            }

            var rowsByProgram = BuildLookup(rows, row => row.ProgramElement);
            var rowsByDepartment = BuildLookup(rows, row => row.Department);

            foreach (var sourceRow in rows)
            {
                foreach (var token in SplitConnections(sourceRow.Connections))
                {
                    var matchingRows = LookupRows(token, rowsByProgram, rowsByDepartment)
                        .Where(row => row.ExcelRow != sourceRow.ExcelRow)
                        .Where(row => string.Equals(row.Floor, sourceRow.Floor, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (matchingRows.Count == 0)
                    {
                        data.Warnings.Add(
                            $"Row {sourceRow.ExcelRow}: connection '{token}' did not match a Program Element or Dept No on floor {sourceRow.Floor}.");
                        continue;
                    }

                    foreach (var sourceNode in nodesByRow[sourceRow.ExcelRow])
                    {
                        foreach (var targetRow in matchingRows)
                        {
                            var targetNodes = nodesByRow[targetRow.ExcelRow];
                            var targetIndex = Math.Min(sourceNode.CopyNumber, targetNodes.Count - 1);
                            data.Connections.Add(new ConnectionPair(sourceNode.Index, targetNodes[targetIndex].Index));
                        }
                    }
                }
            }

            return data;
        }

        private static Dictionary<string, List<ProgramRow>> BuildLookup(
            IEnumerable<ProgramRow> rows,
            Func<ProgramRow, string> selector)
        {
            var lookup = new Dictionary<string, List<ProgramRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var key = NormalizeLookup(selector(row));
                if (string.IsNullOrEmpty(key))
                    continue;
                if (!lookup.TryGetValue(key, out var matches))
                {
                    matches = new List<ProgramRow>();
                    lookup.Add(key, matches);
                }
                matches.Add(row);
            }
            return lookup;
        }

        private static IReadOnlyList<ProgramRow> LookupRows(
            string token,
            IReadOnlyDictionary<string, List<ProgramRow>> byProgram,
            IReadOnlyDictionary<string, List<ProgramRow>> byDepartment)
        {
            var key = NormalizeLookup(token);
            if (byProgram.TryGetValue(key, out var programRows))
                return programRows;
            if (byDepartment.TryGetValue(key, out var departmentRows))
                return departmentRows;
            return Array.Empty<ProgramRow>();
        }

        private static IEnumerable<string> SplitConnections(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            foreach (var token in Regex.Split(value, "[,;|\\r\\n\\\"]+"))
            {
                var cleaned = token.Trim().Trim('-', '>');
                if (!string.IsNullOrWhiteSpace(cleaned) &&
                    !cleaned.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                    !cleaned.Equals("n/a", StringComparison.OrdinalIgnoreCase))
                    yield return cleaned;
            }
        }

        private static (int HeaderRow, Dictionary<string, int> Columns) FindHeaders(
            IXLWorksheet worksheet,
            IXLRange used)
        {
            var firstRow = used.RangeAddress.FirstAddress.RowNumber;
            var lastCandidateRow = Math.Min(
                used.RangeAddress.LastAddress.RowNumber,
                firstRow + 24);
            var firstColumn = used.RangeAddress.FirstAddress.ColumnNumber;
            var lastColumn = used.RangeAddress.LastAddress.ColumnNumber;

            for (var row = firstRow; row <= lastCandidateRow; row++)
            {
                var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    var normalized = NormalizeHeader(CellText(worksheet.Cell(row, column)));
                    if (HeaderAliases.TryGetValue(normalized, out var canonical) &&
                        !columns.ContainsKey(canonical))
                        columns.Add(canonical, column);
                }

                if (RequiredHeaders.All(columns.ContainsKey))
                    return (row, columns);
            }

            throw new InvalidDataException(
                "Could not find a header row containing: " + string.Join(", ", RequiredHeaders));
        }

        private static string CellText(IXLCell cell)
        {
            if (cell == null)
                return string.Empty;

            try
            {
                var formatted = cell.GetFormattedString();
                if (!string.IsNullOrWhiteSpace(formatted))
                    return formatted.Trim();
            }
            catch (Exception)
            {
                // Some formula cells throw; fall through to CachedValue.ToString().
            }

            try
            {
                var cached = cell.CachedValue.ToString();
                return string.IsNullOrWhiteSpace(cached) ? string.Empty : cached.Trim();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool TryParseNumber(string text, out double value)
        {
            var cleaned = text.Replace(",", string.Empty).Trim();
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static Color ParseColor(string text, string fallbackKey)
        {
            var trimmed = text.Trim().Trim('"');
            if (Regex.IsMatch(trimmed, "^#[0-9a-fA-F]{6}$"))
                return ColorTranslator.FromHtml(trimmed);

            var parts = Regex.Split(trimmed, "[,;\\s]+")
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();
            if (parts.Length >= 3 &&
                TryParseNumber(parts[0], out var red) &&
                TryParseNumber(parts[1], out var green) &&
                TryParseNumber(parts[2], out var blue))
            {
                if (red <= 1 && green <= 1 && blue <= 1)
                {
                    red *= 255;
                    green *= 255;
                    blue *= 255;
                }
                return Color.FromArgb(
                    ClampByte(red),
                    ClampByte(green),
                    ClampByte(blue));
            }

            var hash = StableHash(fallbackKey);
            var hue = Math.Abs(hash % 360);
            return ColorFromHsv(hue, 0.55, 0.9);
        }

        private static int ClampByte(double value) =>
            Math.Max(0, Math.Min(255, (int)Math.Round(value)));

        private static int StableHash(string text)
        {
            unchecked
            {
                var hash = 17;
                foreach (var character in text ?? string.Empty)
                    hash = hash * 31 + character;
                return hash;
            }
        }

        private static Color ColorFromHsv(double hue, double saturation, double value)
        {
            var chroma = value * saturation;
            var x = chroma * (1 - Math.Abs((hue / 60.0 % 2) - 1));
            var m = value - chroma;
            var (red, green, blue) = hue switch
            {
                < 60 => (chroma, x, 0.0),
                < 120 => (x, chroma, 0.0),
                < 180 => (0.0, chroma, x),
                < 240 => (0.0, x, chroma),
                < 300 => (x, 0.0, chroma),
                _ => (chroma, 0.0, x)
            };
            return Color.FromArgb(
                ClampByte((red + m) * 255),
                ClampByte((green + m) * 255),
                ClampByte((blue + m) * 255));
        }

        private static string NormalizeHeader(string value) =>
            Regex.Replace(value ?? string.Empty, "[^a-zA-Z0-9]", string.Empty)
                .ToLowerInvariant();

        private static string NormalizeLookup(string value) =>
            Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
    }

    /// <summary>
    /// Kangaroo goal equivalent to the GH Collider behavior needed here, with
    /// one collision radius per program node rather than one shared radius.
    /// </summary>
    internal sealed class VariableRadiusCollisionGoal : GoalObject
    {
        private readonly double[] _radii;
        private readonly double _strength;
        private readonly double _gap;

        public VariableRadiusCollisionGoal(
            IReadOnlyList<Point3d> points,
            IReadOnlyList<double> radii,
            double gap,
            double strength)
        {
            if (points.Count != radii.Count)
                throw new ArgumentException("Point and radius counts must match.");

            PPos = points.ToArray();
            Move = new Vector3d[points.Count];
            Weighting = new double[points.Count];
            _radii = radii.ToArray();
            _gap = gap;
            _strength = strength;
        }

        public int[] ParticleIndices => PIndex;

        public override void Calculate(List<KangarooSolver.Particle> particles)
        {
            for (var index = 0; index < Move.Length; index++)
            {
                Move[index] = Vector3d.Zero;
                Weighting[index] = 0;
            }

            for (var first = 0; first < PIndex.Length - 1; first++)
            {
                for (var second = first + 1; second < PIndex.Length; second++)
                {
                    var separation =
                        particles[PIndex[second]].Position -
                        particles[PIndex[first]].Position;
                    separation.Z = 0;
                    var minimumDistance = _radii[first] + _radii[second] + _gap;
                    var distance = separation.Length;
                    if (distance >= minimumDistance)
                        continue;

                    if (distance < RhinoMath.ZeroTolerance)
                    {
                        separation = Vector3d.XAxis;
                        distance = 1.0;
                    }

                    var correction =
                        0.5 * separation * (1.0 - minimumDistance / distance);
                    Move[first] += correction;
                    Move[second] -= correction;
                    Weighting[first] = _strength;
                    Weighting[second] = _strength;
                }
            }
        }
    }

    internal static class BubbleLayoutEngine
    {
        public static List<FloorLayout> Solve(
            BubbleDiagramData data,
            BubbleDiagramOptions options,
            Curve selectedBoundary,
            double modelTolerance)
        {
            var layouts = new List<FloorLayout>();
            var floorGroups = data.Nodes
                .GroupBy(node => node.Source.Floor, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => FloorSortKey(group.Key))
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var nextOffset = 0.0;
            foreach (var floorGroup in floorGroups)
            {
                var layout = new FloorLayout { Floor = floorGroup.Key };
                layout.Nodes.AddRange(floorGroup);

                var nodeIndices = layout.Nodes.Select(node => node.Index).ToHashSet();
                foreach (var connection in data.Connections)
                {
                    if (nodeIndices.Contains(connection.A) && nodeIndices.Contains(connection.B))
                        layout.Connections.Add(connection);
                }

                layout.Boundary = selectedBoundary?.DuplicateCurve() ??
                                  CreateAutomaticBoundary(layout.Nodes, options.BoundaryScale);
                ValidateBoundary(layout.Boundary);
                SeedGrid(layout.Nodes, layout.Boundary, options.Shape);
                SolveFloor(layout, options, modelTolerance);

                var bounds = layout.Boundary.GetBoundingBox(true);
                var spacing = Math.Max(bounds.Diagonal.X, bounds.Diagonal.Y) * 0.25;
                layout.DocumentOffset = new Vector3d(0, nextOffset - bounds.Min.Y, 0);
                nextOffset += bounds.Diagonal.Y + spacing;
                layouts.Add(layout);
            }

            return layouts;
        }

        private static void SolveFloor(
            FloorLayout layout,
            BubbleDiagramOptions options,
            double modelTolerance)
        {
            var points = layout.Nodes.Select(node => node.Position).ToList();
            var collisionRadii = layout.Nodes
                .Select(node => options.Shape == BubbleShape.Circle
                    ? node.Radius
                    : node.SquareSide * Math.Sqrt(0.5))
                .ToList();

            var collision = new VariableRadiusCollisionGoal(
                points,
                collisionRadii,
                options.BubbleGap,
                options.CollisionStrength);

            var goals = new List<IGoal> { collision };
            var localIndex = layout.Nodes
                .Select((node, index) => (node.Index, index))
                .ToDictionary(item => item.Index, item => item.index);

            foreach (var connection in layout.Connections)
            {
                var first = localIndex[connection.A];
                var second = localIndex[connection.B];
                var restLength = collisionRadii[first] + collisionRadii[second] + options.BubbleGap;
                goals.Add(new Spring(
                    points[first],
                    points[second],
                    restLength,
                    options.ConnectionStrength));
            }

            goals.Add(new Curve2dPoint(
                points,
                layout.Boundary,
                Plane.WorldXY,
                true,
                options.BoundaryStrength));

            var physicalSystem = new PhysicalSystem();
            var assignmentTolerance = Math.Max(modelTolerance, 1e-6);
            foreach (var goal in goals)
                physicalSystem.AssignPIndex(goal, assignmentTolerance);

            physicalSystem.MomentumStep(
                goals,
                Math.Max(0.0, Math.Min(1.0, options.Damping)),
                Math.Max(1, options.Iterations));

            for (var index = 0; index < layout.Nodes.Count; index++)
            {
                var position = physicalSystem.GetPosition(collision.ParticleIndices[index]);
                position.Z = 0;
                layout.Nodes[index].Position = position;
            }
        }

        private static void SeedGrid(
            IReadOnlyList<BubbleNode> nodes,
            Curve boundary,
            BubbleShape shape)
        {
            var box = boundary.GetBoundingBox(true);
            var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(
                nodes.Count * Math.Max(box.Diagonal.X, 1e-6) /
                Math.Max(box.Diagonal.Y, 1e-6))));
            var rows = Math.Max(1, (int)Math.Ceiling(nodes.Count / (double)columns));
            var xStep = box.Diagonal.X / columns;
            var yStep = box.Diagonal.Y / rows;

            _ = shape;
            for (var index = 0; index < nodes.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var point = new Point3d(
                    box.Min.X + (column + 0.5) * xStep,
                    box.Min.Y + (row + 0.5) * yStep,
                    0);

                if (!IsInside(boundary, point))
                {
                    var closest = boundary.ClosestPoint(point, out var parameter)
                        ? boundary.PointAt(parameter)
                        : box.Center;
                    point = 0.8 * box.Center + 0.2 * closest;
                }

                nodes[index].Position = point;
            }
        }

        private static bool IsInside(Curve boundary, Point3d point) =>
            boundary.Contains(point, Plane.WorldXY, RhinoMath.SqrtEpsilon) !=
            PointContainment.Outside;

        private static Curve CreateAutomaticBoundary(
            IReadOnlyCollection<BubbleNode> nodes,
            double boundaryScale)
        {
            var occupiedArea = nodes.Sum(node => Math.PI * node.Radius * node.Radius);
            var targetArea = Math.Max(occupiedArea * boundaryScale * boundaryScale, 1.0);
            const double aspectRatio = 1.6;
            var width = Math.Sqrt(targetArea * aspectRatio);
            var height = targetArea / width;
            var rectangle = new Rectangle3d(
                Plane.WorldXY,
                new Interval(-width * 0.5, width * 0.5),
                new Interval(-height * 0.5, height * 0.5));
            return rectangle.ToNurbsCurve();
        }

        private static void ValidateBoundary(Curve boundary)
        {
            if (!boundary.IsClosed || !boundary.IsPlanar())
                throw new ArgumentException("The boundary must be a closed planar curve.");

            var box = boundary.GetBoundingBox(true);
            if (box.Diagonal.X <= RhinoMath.ZeroTolerance ||
                box.Diagonal.Y <= RhinoMath.ZeroTolerance)
                throw new ArgumentException("The boundary must enclose a non-zero XY area.");
        }

        private static double FloorSortKey(string floor) =>
            double.TryParse(floor, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : double.MaxValue;
    }

    internal static class BubbleDiagramWriter
    {
        public static void AddToDocument(
            RhinoDoc document,
            IReadOnlyList<FloorLayout> layouts,
            BubbleDiagramOptions options)
        {
            var rootLayer = EnsureLayer(document, "Excel Bubble Diagram", Guid.Empty, Color.DimGray);

            foreach (var layout in layouts)
            {
                var safeFloor = SanitizeLayerName(layout.Floor);
                var floorLayer = EnsureLayer(
                    document,
                    $"Floor {safeFloor}",
                    document.Layers[rootLayer].Id,
                    Color.Gray);
                var floorLayerId = document.Layers[floorLayer].Id;
                var bubbleLayer = EnsureLayer(document, "Bubbles", floorLayerId, Color.SteelBlue);
                var connectionLayer = EnsureLayer(document, "Connections", floorLayerId, Color.DarkGray);
                var labelLayer = EnsureLayer(document, "Labels", floorLayerId, Color.Black);
                var boundaryLayer = EnsureLayer(document, "Boundary", floorLayerId, Color.Gray);

                var translatedPositions = layout.Nodes.ToDictionary(
                    node => node.Index,
                    node => node.Position + layout.DocumentOffset);

                var uidLookup = layout.Nodes.ToDictionary(node => node.Index, node => node.Uid);

                foreach (var node in layout.Nodes)
                {
                    var center = translatedPositions[node.Index];
                    var outline = CreateOutline(center, node, options.Shape);
                    var bubbleAttributes = Attributes(
                        bubbleLayer,
                        node.Source.Color,
                        $"{node.Source.Department} - {node.DisplayName}");
                    StampBubbleMetadata(bubbleAttributes, node, layout, uidLookup);
                    document.Objects.AddCurve(outline, bubbleAttributes);

                    var label = new TextEntity
                    {
                        Plane = new Plane(center, Vector3d.ZAxis),
                        PlainText =
                            $"{node.Source.Department}\n{node.DisplayName}\n" +
                            $"{node.Source.NetSquareFeet:0.#} SF",
                        TextHeight = Math.Max(
                            document.ModelAbsoluteTolerance * 10,
                            Math.Min(node.Radius, node.SquareSide * 0.5) * 0.18),
                        Justification = TextJustification.MiddleCenter
                    };
                    document.Objects.AddText(
                        label,
                        Attributes(labelLayer, Color.Black, $"{node.DisplayName} label"));
                }

                foreach (var connection in layout.Connections)
                {
                    var line = new LineCurve(
                        translatedPositions[connection.A],
                        translatedPositions[connection.B]);
                    document.Objects.AddCurve(
                        line,
                        Attributes(connectionLayer, Color.DarkSlateGray, "Primary connection"));
                }

                var boundary = layout.Boundary.DuplicateCurve();
                boundary.Transform(Transform.Translation(layout.DocumentOffset));
                document.Objects.AddCurve(
                    boundary,
                    Attributes(boundaryLayer, Color.Gray, $"Floor {layout.Floor} boundary"));
            }

            document.Views.Redraw();
        }

        private static Curve CreateOutline(
            Point3d center,
            BubbleNode node,
            BubbleShape shape)
        {
            if (shape == BubbleShape.Circle)
                return new Circle(Plane.WorldXY, center, node.Radius).ToNurbsCurve();

            var half = node.SquareSide * 0.5;
            return new Rectangle3d(
                new Plane(center, Vector3d.ZAxis),
                new Interval(-half, half),
                new Interval(-half, half)).ToNurbsCurve();
        }

        private static void StampBubbleMetadata(
            ObjectAttributes attributes,
            BubbleNode node,
            FloorLayout layout,
            IReadOnlyDictionary<int, string> uidLookup)
        {
            attributes.SetUserString("UID", node.Uid);
            attributes.SetUserString("Area", node.Source.NetSquareFeet.ToString(CultureInfo.InvariantCulture));
            attributes.SetUserString("Label", node.DisplayName);
            attributes.SetUserString("Dept", node.Source.Department);
            attributes.SetUserString(
                "FloorPref",
                int.TryParse(node.Source.Floor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var floor) && floor > 0
                    ? floor.ToString(CultureInfo.InvariantCulture)
                    : "0");

            var adjacent = layout.Connections
                .Where(connection => connection.A == node.Index || connection.B == node.Index)
                .Select(connection => connection.A == node.Index ? connection.B : connection.A)
                .Select(index => uidLookup[index])
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(adjacent))
                attributes.SetUserString("AdjacentTo", adjacent);
        }

        private static ObjectAttributes Attributes(int layer, Color color, string name) =>
            new()
            {
                LayerIndex = layer,
                ColorSource = ObjectColorSource.ColorFromObject,
                ObjectColor = color,
                Name = name
            };

        private static int EnsureLayer(
            RhinoDoc document,
            string name,
            Guid parentId,
            Color color)
        {
            foreach (var existing in document.Layers)
            {
                if (!existing.IsDeleted &&
                    existing.ParentLayerId == parentId &&
                    existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return existing.Index;
            }

            var layer = new Layer
            {
                Name = name,
                ParentLayerId = parentId,
                Color = color
            };
            var index = document.Layers.Add(layer);
            if (index < 0)
                throw new InvalidOperationException($"Could not create layer '{name}'.");
            return index;
        }

        private static string SanitizeLayerName(string value)
        {
            var invalid = new[] { ':', ';', '{', '}', '?', '`' };
            var builder = new StringBuilder(value);
            foreach (var character in invalid)
                builder.Replace(character, '_');
            return builder.ToString().Trim();
        }
    }

    public sealed class ExcelToBubbleDiagramCommand : Command
    {
        public override string EnglishName => "ExcelToBubbleDiagram";

        protected override Result RunCommand(RhinoDoc document, RunMode mode)
        {
            var workbookPath = SelectWorkbook();
            if (string.IsNullOrWhiteSpace(workbookPath))
                return Result.Cancel;

            var options = new BubbleDiagramOptions();
            if (!GetWorksheetName(options) || !GetOptions(options))
                return Result.Cancel;

            var boundaryResult = GetOptionalBoundary(out var selectedBoundary);
            if (boundaryResult == Result.Cancel)
                return Result.Cancel;

            uint undo = 0;
            try
            {
                var feetToModel = RhinoMath.UnitScale(UnitSystem.Feet, document.ModelUnitSystem);
                if (!RhinoMath.IsValidDouble(feetToModel) || feetToModel <= 0)
                    feetToModel = 1.0;

                RhinoApp.WriteLine($"Reading '{Path.GetFileName(workbookPath)}'...");
                var data = BubbleDiagramExcelReader.Load(
                    workbookPath,
                    options.WorksheetName,
                    feetToModel);

                foreach (var warning in data.Warnings.Distinct())
                    RhinoApp.WriteLine("Warning: " + warning);

                RhinoApp.WriteLine(
                    $"Solving {data.Nodes.Count} rooms on {data.FloorCount} floors...");
                var layouts = BubbleLayoutEngine.Solve(
                    data,
                    options,
                    selectedBoundary,
                    document.ModelAbsoluteTolerance);

                undo = document.BeginUndoRecord("Excel To Bubble Diagram");
                BubbleDiagramWriter.AddToDocument(document, layouts, options);

                RhinoApp.WriteLine(
                    $"Excel bubble diagram created: {data.Nodes.Count} rooms, " +
                    $"{data.FloorCount} floors, {data.TotalSquareFeet:0.##} total SF, " +
                    $"{data.Connections.Count} primary connections.");
                foreach (var floor in data.FloorTotals())
                    RhinoApp.WriteLine(
                        $"  Floor {floor.Floor}: {floor.RoomCount} rooms, {floor.SquareFeet:0.##} SF");
                return Result.Success;
            }
            catch (Exception exception)
            {
                RhinoApp.WriteLine("ExcelToBubbleDiagram failed: " + exception.Message);
                return Result.Failure;
            }
            finally
            {
                if (undo != 0)
                    document.EndUndoRecord(undo);
                selectedBoundary?.Dispose();
            }
        }

        private static string SelectWorkbook()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select the program workbook",
                Filter = "Excel workbooks (*.xlsx;*.xlsm)|*.xlsx;*.xlsm",
                DefaultExt = "xlsm"
            };
            return dialog.ShowOpenDialog() ? dialog.FileName : null;
        }

        private static bool GetWorksheetName(BubbleDiagramOptions options)
        {
            using var getString = new GetString();
            getString.SetCommandPrompt("Excel worksheet name");
            getString.SetDefaultString(options.WorksheetName);
            getString.AcceptNothing(true);
            var result = getString.Get();
            if (result == GetResult.Cancel)
                return false;
            if (result == GetResult.String && !string.IsNullOrWhiteSpace(getString.StringResult()))
                options.WorksheetName = getString.StringResult();
            return true;
        }

        private static bool GetOptions(BubbleDiagramOptions options)
        {
            var shapeIndex = (int)options.Shape;
            var scale = new OptionDouble(options.BoundaryScale, 1.0, 10.0);
            var damping = new OptionDouble(options.Damping, 0.0, 1.0);
            var iterations = new OptionInteger(options.Iterations, 1, 100000);

            while (true)
            {
                using var getOption = new GetOption();
                getOption.SetCommandPrompt("Bubble diagram settings; press Enter when done");
                getOption.AcceptNothing(true);
                var shapeOption = getOption.AddOptionList(
                    "Shape",
                    new[] { "Circle", "Square" },
                    shapeIndex);
                getOption.AddOptionDouble("BoundaryScale", ref scale);
                getOption.AddOptionDouble("Damping", ref damping);
                getOption.AddOptionInteger("Iterations", ref iterations);

                var result = getOption.Get();
                if (result == GetResult.Cancel)
                    return false;
                if (result == GetResult.Nothing)
                    break;
                if (result == GetResult.Option &&
                    getOption.OptionIndex() == shapeOption)
                    shapeIndex = getOption.Option().CurrentListOptionIndex;
            }

            options.Shape = (BubbleShape)shapeIndex;
            options.BoundaryScale = scale.CurrentValue;
            options.Damping = damping.CurrentValue;
            options.Iterations = iterations.CurrentValue;
            return true;
        }

        private static Result GetOptionalBoundary(out Curve boundary)
        {
            boundary = null;
            using var getObject = new GetObject();
            getObject.SetCommandPrompt(
                "Select a closed planar boundary curve, or press Enter to generate one");
            getObject.GeometryFilter = ObjectType.Curve;
            getObject.SubObjectSelect = false;
            getObject.AcceptNothing(true);
            var result = getObject.Get();

            if (result == GetResult.Cancel)
                return Result.Cancel;
            if (result == GetResult.Nothing)
                return Result.Success;
            if (result != GetResult.Object)
                return Result.Failure;

            boundary = getObject.Object(0).Curve()?.DuplicateCurve();
            if (boundary == null || !boundary.IsClosed || !boundary.IsPlanar())
            {
                boundary?.Dispose();
                boundary = null;
                RhinoApp.WriteLine("The selected object is not a closed planar curve.");
                return Result.Failure;
            }

            var transform = Transform.PlanarProjection(Plane.WorldXY);
            boundary.Transform(transform);
            return Result.Success;
        }
    }
}
