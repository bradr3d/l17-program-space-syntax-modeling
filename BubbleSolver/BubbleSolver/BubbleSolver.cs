// ==============================================================================
// BUBBLE DIAGRAM SOLVER (force relaxation, per-floor)
// ------------------------------------------------------------------------------
// Pure C#. No RhinoCommon reference, by design -- this is the "Solver" layer
// of the five-layer architecture, and it must be testable from a console app
// with no CAD installed. The "Adapter" layer (separate project) is responsible
// for turning a BubbleSolverResult into Rhino geometry.
//
// Direct translation of the Grasshopper CPython prototype (Component 02):
//   - Rooms become circles sized to their target area.
//   - Adjacency pulls (directed spring) draw a room toward its target(s).
//   - Repulsion prevents overlap, within a floor only.
//   - Floor cohesion keeps each floor's rooms from drifting apart.
//   - A dedicated de-overlap pass (repulsion only) cleans up residual overlap
//     after the main relaxation loop.
// ==============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace BubbleSolver
{
    /// <summary>One room instance going into the bubble solver.</summary>
    public sealed class RoomInput
    {
        public string Uid { get; init; } = "";
        public double Area { get; init; }          // in UnitSystem units^2
        public int Floor { get; init; }
        public string Label { get; init; } = "";
        public int Dept { get; init; } = -1;
    }

    /// <summary>A directed adjacency preference: Source should be near Target.</summary>
    public readonly record struct AdjacencyEdge(string Source, string Target);

    public enum LinearUnit { Feet, Meters }

    /// <summary>Tunable solver parameters -- the natural candidates for UI sliders.</summary>
    public sealed class BubbleSolverConfig
    {
        public LinearUnit UnitSystem { get; init; } = LinearUnit.Feet;
        public int Iterations { get; init; } = 400;
        public int DeoverlapIterations { get; init; } = 120;
        public double KAdjacency { get; init; } = 0.08;   // spring stiffness
        public double KRepel { get; init; } = 0.85;        // overlap repulsion stiffness
        public double KFloorCohesion { get; init; } = 0.015; // pull to floor centroid
        public double Padding { get; init; } = 2.0;         // min gap between bubble edges (input units)
        public int Seed { get; init; } = 42;
        public double OverlapTolerance { get; init; } = 0.01; // input units of slop
    }

    /// <summary>Resolved position of one room after relaxation (floor-local coordinates).</summary>
    public sealed class BubbleResult
    {
        public string Uid { get; init; } = "";
        public int Floor { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Radius { get; init; }
        public string Label { get; init; } = "";
        public int Dept { get; init; }
    }

    public sealed class FloorEnvelope
    {
        public int Floor { get; init; }
        public double MinX { get; init; }
        public double MinY { get; init; }
        public double MaxX { get; init; }
        public double MaxY { get; init; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }

    public sealed class BubbleSolverResult
    {
        public List<BubbleResult> Rooms { get; init; } = new();
        public List<FloorEnvelope> FloorEnvelopes { get; init; } = new();
        public double MaxResidualOverlap { get; init; }   // reported in input units; ~0 means clean
        public bool Converged { get; init; }
        public string Info { get; init; } = "";

        public IEnumerable<BubbleResult> OnFloor(int floor) => Rooms.Where(r => r.Floor == floor);
    }

    public static class BubbleSolver
    {
        /// <summary>
        /// Relax a flat room list into a non-overlapping, adjacency-respecting
        /// bubble diagram, grouped per floor. Mirrors the Grasshopper prototype's
        /// physics exactly: directed adjacency springs, same-floor repulsion,
        /// floor cohesion, clamped integration, then a dedicated de-overlap pass.
        /// </summary>
        public static BubbleSolverResult Solve(
            IReadOnlyList<RoomInput> rooms,
            IReadOnlyList<AdjacencyEdge> edges,
            BubbleSolverConfig? config = null)
        {
            config ??= new BubbleSolverConfig();

            int n = rooms.Count;
            if (n == 0)
            {
                return new BubbleSolverResult { Info = "No rooms supplied." };
            }

            // ---- unit conversion: input units -> arbitrary "model" units -------
            // (In the prototype this tracked the Rhino document's unit system.
            //  Here we just normalize feet/meters to a single internal scale;
            //  the Adapter layer maps this to real Rhino model units.)
            double linScale = config.UnitSystem == LinearUnit.Meters ? 1.0 : 0.3048;
            double pad = config.Padding * linScale;

            // ---- per-room arrays ------------------------------------------------
            var uid = rooms.Select(r => r.Uid).ToArray();
            var radius = rooms.Select(r => AreaToRadius(r.Area, linScale)).ToArray();
            var floor = rooms.Select(r => r.Floor).ToArray();
            var label = rooms.Select(r => r.Label).ToArray();
            var dept = rooms.Select(r => r.Dept).ToArray();

            var idxOf = new Dictionary<string, int>(n);
            for (int i = 0; i < n; i++) idxOf[uid[i]] = i;

            var floors = floor.Distinct().OrderBy(f => f).ToArray();
            var membersOf = floors.ToDictionary(
                f => f,
                f => Enumerable.Range(0, n).Where(i => floor[i] == f).ToArray());

            // ---- resolve adjacency edges to index pairs, skipping unknown uids -
            var edgePairs = new List<(int a, int b)>();
            foreach (var e in edges)
            {
                if (idxOf.TryGetValue(e.Source, out int a) && idxOf.TryGetValue(e.Target, out int b))
                    edgePairs.Add((a, b));
            }

            // ---- initial placement: random ring per floor -----------------------
            var rng = new Random(config.Seed);
            var px = new double[n];
            var py = new double[n];
            foreach (var f in floors)
            {
                var members = membersOf[f];
                double totalArea = members.Sum(i => Math.PI * radius[i] * radius[i]);
                double ringRadius = Math.Sqrt(totalArea / Math.PI) * 1.3 + 1.0;
                foreach (var i in members)
                {
                    double ang = rng.NextDouble() * 2 * Math.PI;
                    double rr = rng.NextDouble() * ringRadius;
                    px[i] = rr * Math.Cos(ang);
                    py[i] = rr * Math.Sin(ang);
                }
            }

            // Step clamp: a bubble can't move more than half the smallest radius
            // in one iteration -- prevents overshoot that leaves frozen overlap.
            double maxStep = 0.5 * radius.Min();

            // ---- main relaxation loop --------------------------------------------
            var fx = new double[n];
            var fy = new double[n];
            for (int iter = 0; iter < Math.Max(1, config.Iterations); iter++)
            {
                Array.Clear(fx, 0, n);
                Array.Clear(fy, 0, n);

                // 1) adjacency springs (directed: source pulled toward target,
                //    target nudged half as much back, for stable convergence)
                foreach (var (a, b) in edgePairs)
                {
                    double dx = px[b] - px[a];
                    double dy = py[b] - py[a];
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-6) continue;

                    double rest = radius[a] + radius[b] + pad;
                    double f = config.KAdjacency * (d - rest);
                    double ux = dx / d, uy = dy / d;

                    fx[a] += f * ux;
                    fy[a] += f * uy;
                    fx[b] -= 0.5 * f * ux;
                    fy[b] -= 0.5 * f * uy;
                }

                // 2) non-overlap repulsion, within the same floor only
                foreach (var f in floors)
                {
                    var members = membersOf[f];
                    int m = members.Length;
                    for (int ii = 0; ii < m; ii++)
                    {
                        int a = members[ii];
                        for (int jj = ii + 1; jj < m; jj++)
                        {
                            int b = members[jj];
                            double dx = px[b] - px[a];
                            double dy = py[b] - py[a];
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            double minD = radius[a] + radius[b] + pad;

                            if (d < 1e-6)
                            {
                                dx = rng.NextDouble() * 2 - 1;
                                dy = rng.NextDouble() * 2 - 1;
                                d = 1.0;
                            }
                            if (d < minD)
                            {
                                double overlap = minD - d;
                                double ux = dx / d, uy = dy / d;
                                double f2 = config.KRepel * overlap;
                                fx[a] -= f2 * ux; fy[a] -= f2 * uy;
                                fx[b] += f2 * ux; fy[b] += f2 * uy;
                            }
                        }
                    }
                }

                // 3) floor cohesion: gentle pull to each floor's centroid
                foreach (var f in floors)
                {
                    var members = membersOf[f];
                    if (members.Length == 0) continue;
                    double cx = members.Average(i => px[i]);
                    double cy = members.Average(i => py[i]);
                    foreach (var i in members)
                    {
                        fx[i] += config.KFloorCohesion * (cx - px[i]);
                        fy[i] += config.KFloorCohesion * (cy - py[i]);
                    }
                }

                // integrate, clamped
                for (int i = 0; i < n; i++)
                {
                    double sx = fx[i], sy = fy[i];
                    double sLen = Math.Sqrt(sx * sx + sy * sy);
                    if (sLen > maxStep && sLen > 1e-9)
                    {
                        sx *= maxStep / sLen;
                        sy *= maxStep / sLen;
                    }
                    px[i] += sx;
                    py[i] += sy;
                }
            }

            // ---- de-overlap pass: repulsion only, until converged or capped -----
            double tol = config.OverlapTolerance * linScale;
            double maxOverlap = 0.0;
            bool converged = false;

            for (int pass = 0; pass < Math.Max(0, config.DeoverlapIterations); pass++)
            {
                double worst = 0.0;
                foreach (var f in floors)
                {
                    var members = membersOf[f];
                    int m = members.Length;
                    for (int ii = 0; ii < m; ii++)
                    {
                        int a = members[ii];
                        for (int jj = ii + 1; jj < m; jj++)
                        {
                            int b = members[jj];
                            double dx = px[b] - px[a];
                            double dy = py[b] - py[a];
                            double d = Math.Sqrt(dx * dx + dy * dy);
                            double minD = radius[a] + radius[b] + pad;

                            if (d < 1e-9)
                            {
                                double ang = rng.NextDouble() * 2 * Math.PI;
                                dx = Math.Cos(ang); dy = Math.Sin(ang); d = 1.0;
                            }
                            if (d < minD)
                            {
                                double overlap = minD - d;
                                if (overlap > worst) worst = overlap;
                                double ux = dx / d, uy = dy / d;
                                double shift = 0.5 * overlap;
                                px[a] -= shift * ux; py[a] -= shift * uy;
                                px[b] += shift * ux; py[b] += shift * uy;
                            }
                        }
                    }
                }
                maxOverlap = worst;
                if (worst <= tol) { converged = true; break; }
            }
            // report in input units, not internal scale
            double maxOverlapReported = linScale > 0 ? maxOverlap / linScale : maxOverlap;

            // ---- re-center each floor to its own local origin --------------------
            foreach (var f in floors)
            {
                var members = membersOf[f];
                if (members.Length == 0) continue;
                double cx = members.Average(i => px[i]);
                double cy = members.Average(i => py[i]);
                foreach (var i in members)
                {
                    px[i] -= cx;
                    py[i] -= cy;
                }
            }

            // ---- package results --------------------------------------------------
            var resultRooms = new List<BubbleResult>(n);
            var envelopes = new List<FloorEnvelope>(floors.Length);

            foreach (var f in floors)
            {
                var members = membersOf[f];
                foreach (var i in members)
                {
                    resultRooms.Add(new BubbleResult
                    {
                        Uid = uid[i],
                        Floor = f,
                        X = px[i],
                        Y = py[i],
                        Radius = radius[i],
                        Label = label[i],
                        Dept = dept[i]
                    });
                }

                if (members.Length > 0)
                {
                    envelopes.Add(new FloorEnvelope
                    {
                        Floor = f,
                        MinX = members.Min(i => px[i] - radius[i]),
                        MaxX = members.Max(i => px[i] + radius[i]),
                        MinY = members.Min(i => py[i] - radius[i]),
                        MaxY = members.Max(i => py[i] + radius[i]),
                    });
                }
            }

            string info = BuildInfo(n, floors.Length, edgePairs.Count, config,
                                     converged, maxOverlapReported, membersOf);

            return new BubbleSolverResult
            {
                Rooms = resultRooms,
                FloorEnvelopes = envelopes,
                MaxResidualOverlap = maxOverlapReported,
                Converged = converged,
                Info = info
            };
        }

        private static double AreaToRadius(double areaInput, double linScale)
        {
            if (areaInput <= 0) 
                areaInput = 1.0;
            double areaModel = areaInput * (linScale * linScale);
            return Math.Sqrt(areaModel / Math.PI);
        }

        private static string BuildInfo(
            int n, int floorCount, int edgeCount, BubbleSolverConfig cfg,
            bool converged, double maxOverlap, Dictionary<int, int[]> membersOf)
        {
            var lines = new List<string>
            {
                "Bubble Solver",
                $"Rooms: {n}   Floors: {floorCount}   Edges: {edgeCount}",
                $"Unit system: {cfg.UnitSystem}   iterations: {cfg.Iterations}",
                $"kAdj={cfg.KAdjacency}  kRepel={cfg.KRepel}  kFloor={cfg.KFloorCohesion}  pad={cfg.Padding}",
                $"De-overlap: {(converged ? "converged" : "CAP HIT (raise DeoverlapIterations)")}" +
                $"   max residual overlap: {maxOverlap:F4} {cfg.UnitSystem}  {(converged ? "OK" : "<-- still overlapping")}"
            };
            foreach (var kv in membersOf.OrderBy(kv => kv.Key))
                lines.Add($"  Floor {kv.Key} : {kv.Value.Length} rooms");

            return string.Join(Environment.NewLine, lines);
        }
    }
}
