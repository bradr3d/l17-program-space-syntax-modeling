using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BubbleSolver;

namespace ExcelToBubbleDiagram
{
    // Keeps workbook conventions and validation outside the imported solver.
    internal static class BubbleSolverAdapter
    {
        internal const double MetersPerFoot = 0.3048;

        public static BubbleSolverResult Solve(BubbleDiagramData data, BubbleDiagramOptions options)
        {
            if (data == null || data.Nodes.Count == 0)
                throw new ArgumentException("Supply at least one room to the bubble solver.", nameof(data));
            if (options == null || options.Iterations <= 0 || options.DeoverlapIterations <= 0 ||
                !double.IsFinite(options.SolverPaddingFeet) || options.SolverPaddingFeet < 0)
                throw new ArgumentException("Solver iterations must be positive and padding must be finite and non-negative.", nameof(options));

            var byIndex = new Dictionary<int, BubbleNode>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in data.Nodes)
            {
                if (node.Source == null || !double.IsFinite(node.Source.NetSquareFeet) || node.Source.NetSquareFeet <= 0)
                    throw new ArgumentException("Each solver room must have a finite, positive area.", nameof(data));
                if (!byIndex.TryAdd(node.Index, node) || !ids.Add(node.Uid))
                    throw new ArgumentException("Bubble room indices and UIDs must be unique. Check repeated Dept No values.", nameof(data));
            }

            // Floor labels may be text. The integer IDs are local to this solve.
            var floors = data.FloorTotals().Select((floor, index) => (floor.Floor, Id: index + 1))
                .ToDictionary(item => item.Floor, item => item.Id, StringComparer.OrdinalIgnoreCase);
            var rooms = data.Nodes.Select(node => new RoomInput
            {
                Uid = node.Uid,
                Area = node.Source.NetSquareFeet,
                Floor = floors[node.Source.Floor],
                Label = node.DisplayName,
                Dept = DepartmentToken(node.Source.Department)
            }).ToList();

            var edges = new List<AdjacencyEdge>();
            foreach (var pair in data.Connections.OrderBy(pair => pair.A).ThenBy(pair => pair.B))
            {
                if (!byIndex.TryGetValue(pair.A, out var first) || !byIndex.TryGetValue(pair.B, out var second) ||
                    first.Index == second.Index)
                    throw new ArgumentException("A bubble connection references a missing room or itself.", nameof(data));
                if (!string.Equals(first.Source.Floor, second.Source.Floor, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Bubble connections must stay on the same floor.", nameof(data));

                // The existing reader stores undirected pairs. Balance both directions.
                edges.Add(new AdjacencyEdge(first.Uid, second.Uid));
                edges.Add(new AdjacencyEdge(second.Uid, first.Uid));
            }

            var config = new BubbleSolverConfig
            {
                UnitSystem = LinearUnit.Feet,
                Iterations = options.Iterations,
                DeoverlapIterations = options.DeoverlapIterations,
                Padding = options.SolverPaddingFeet
            };
            var result = BubbleSolver.BubbleSolver.Solve(rooms, edges, config);
            ValidateResult(rooms, result, config);
            return result;
        }

        internal static int DepartmentToken(string department)
        {
            var head = (department ?? string.Empty).Split('.')[0].Trim();
            return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var token) && token >= 0
                ? token
                : -1;
        }

        private static void ValidateResult(
            IReadOnlyList<RoomInput> input, BubbleSolverResult result, BubbleSolverConfig config)
        {
            if (result.Rooms.Count != input.Count)
                throw new InvalidOperationException("The bubble solver returned an unexpected room count.");

            var expected = input.ToDictionary(room => room.Uid, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in result.Rooms)
            {
                if (room.Uid == null || !expected.TryGetValue(room.Uid, out var source) || !seen.Add(room.Uid) ||
                    room.Floor != source.Floor || !double.IsFinite(room.X) || !double.IsFinite(room.Y) ||
                    !double.IsFinite(room.Radius) || room.Radius <= 0)
                    throw new InvalidOperationException("The bubble solver returned invalid room geometry or identity.");
                var expectedRadius = Math.Sqrt(source.Area / Math.PI) * MetersPerFoot;
                if (Math.Abs(room.Radius - expectedRadius) > expectedRadius * 1e-10)
                    throw new InvalidOperationException("The bubble solver returned an unexpected room radius.");
            }

            // Check final positions independently of the solver's reported convergence.
            var worst = 0.0;
            foreach (var floor in result.Rooms.GroupBy(room => room.Floor))
            {
                var members = floor.ToList();
                for (var first = 0; first < members.Count; first++)
                {
                    for (var second = first + 1; second < members.Count; second++)
                    {
                        var a = members[first];
                        var b = members[second];
                        var dx = a.X - b.X;
                        var dy = a.Y - b.Y;
                        var deficit = a.Radius + b.Radius + config.Padding * MetersPerFoot -
                                      Math.Sqrt(dx * dx + dy * dy);
                        worst = Math.Max(worst, deficit / MetersPerFoot);
                    }
                }
            }

            if (!result.Converged || !double.IsFinite(result.MaxResidualOverlap) ||
                worst > config.OverlapTolerance + 1e-8)
                throw new InvalidOperationException(
                    $"Bubble clearance did not converge (gap deficit {worst:0.####} feet). " +
                    "Increase CleanupPasses and try again.");
        }
    }
}
