using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal static class LayoutEngine
    {
        private sealed class Group
        {
            public readonly List<BubbleRoom> Rooms = new List<BubbleRoom>();
            public double Area { get { return Rooms.Sum(r => r.Area); } }
            public int Preference
            {
                get
                {
                    var preferences = Rooms.Where(r => r.FloorPreference > 0)
                        .Select(r => r.FloorPreference).ToList();
                    return preferences.Count == 0 ? 0 : preferences.Min();
                }
            }
        }

        private sealed class ShapedRoom
        {
            public BubbleRoom Room;
            public double Width;
            public double Depth;
        }

        private sealed class BranchLayout
        {
            public readonly List<ShapedRoom> Top = new List<ShapedRoom>();
            public readonly List<ShapedRoom> Bottom = new List<ShapedRoom>();
            public double Width;
        }

        private sealed class ClusterLayout
        {
            public int Id;
            public readonly List<BranchLayout> Branches = new List<BranchLayout>();
            public double Width;
            public double Height;
        }

        public static FloorplanResult Generate(IList<BubbleRoom> rooms, GenerationSettings settings)
        {
            if (rooms == null || rooms.Count == 0)
                throw new ArgumentException("At least one bubble room is required.");

            var result = new FloorplanResult();
            var floorByUid = AssignFloors(rooms, settings, result.Warnings);
            var floors = floorByUid.Values.Distinct().OrderBy(f => f).ToList();
            result.FloorCount = floors.Count;

            for (var floorIndex = 0; floorIndex < floors.Count; floorIndex++)
            {
                var floor = floors[floorIndex];
                var floorRooms = rooms.Where(r => floorByUid[r.Uid] == floor).ToList();
                var clusterByUid = Cluster(floorRooms, settings, settings.Seed + floor);
                var floorOrigin = settings.Origin + new Vector3d(
                    floorIndex * settings.Model(settings.PlateWidth + settings.FloorGap), 0, 0);
                OrganizeFloor(floorRooms, floor, clusterByUid, floorOrigin, settings, result);
            }

            return result;
        }

        internal sealed class LayoutAnalysis
        {
            public Dictionary<string, int> FloorByUid;
            public Dictionary<string, int> ClusterByUid;
            public Dictionary<string, double> WidthByUid;
            public Dictionary<string, double> DepthByUid;
            public Dictionary<int, List<string>> PackingOrderByCluster;
        }

        internal static LayoutAnalysis Analyze(IList<BubbleRoom> rooms, GenerationSettings settings)
        {
            var warnings = new List<string>();
            var floorByUid = AssignFloors(rooms, settings, warnings);
            var clusterByUid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var widthByUid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var depthByUid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var packing = new Dictionary<int, List<string>>();

            foreach (var floor in floorByUid.Values.Distinct().OrderBy(f => f))
            {
                var floorRooms = rooms.Where(r => floorByUid[r.Uid] == floor).ToList();
                var clusters = Cluster(floorRooms, settings, settings.Seed + floor);
                foreach (var pair in clusters)
                    clusterByUid[pair.Key] = pair.Value;
                foreach (var clusterId in clusters.Values.Distinct().OrderBy(x => x))
                {
                    var members = floorRooms.Where(r => clusters[r.Uid] == clusterId).ToList();
                    var layout = BuildCluster(clusterId, members, settings);
                    var order = new List<string>();
                    foreach (var branch in layout.Branches)
                    {
                        foreach (var room in branch.Top.Concat(branch.Bottom))
                        {
                            widthByUid[room.Room.Uid] = room.Width;
                            depthByUid[room.Room.Uid] = room.Depth;
                            order.Add(room.Room.Uid);
                        }
                    }
                    packing[clusterId] = order;
                }
            }

            return new LayoutAnalysis
            {
                FloorByUid = floorByUid,
                ClusterByUid = clusterByUid,
                WidthByUid = widthByUid,
                DepthByUid = depthByUid,
                PackingOrderByCluster = packing
            };
        }

        private static Dictionary<string, int> AssignFloors(
            IList<BubbleRoom> rooms,
            GenerationSettings settings,
            IList<string> warnings)
        {
            var parent = rooms.ToDictionary(r => r.Uid, r => r.Uid, StringComparer.OrdinalIgnoreCase);
            Func<string, string> find = null;
            find = delegate(string uid)
            {
                var p = parent[uid];
                if (!p.Equals(uid, StringComparison.OrdinalIgnoreCase))
                    parent[uid] = find(p);
                return parent[uid];
            };
            Action<string, string> union = delegate(string a, string b)
            {
                var ra = find(a);
                var rb = find(b);
                if (!ra.Equals(rb, StringComparison.OrdinalIgnoreCase))
                    parent[ra] = rb;
            };

            var roomByUid = rooms.ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);
            foreach (var room in rooms)
                if (room.Shared && !string.IsNullOrWhiteSpace(room.AdjacentTo))
                    union(room.Uid, room.AdjacentTo);

            var groups = rooms.GroupBy(r => find(r.Uid), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var group = new Group();
                    group.Rooms.AddRange(g);
                    return group;
                }).OrderByDescending(g => g.Area).ToList();

            var capacity = settings.PlateWidth * settings.PlateLength * settings.FloorEfficiency;
            if (capacity <= 0)
                throw new ArgumentException("Plate capacity must be positive.");

            var used = new Dictionary<int, double>();
            var groupFloor = new Dictionary<Group, int>();
            Func<Group, int, bool> fits = (g, f) =>
                (!used.ContainsKey(f) ? 0 : used[f]) + g.Area <= capacity + 1e-8;
            Action<Group, int> place = delegate(Group g, int f)
            {
                groupFloor[g] = f;
                used[f] = (!used.ContainsKey(f) ? 0 : used[f]) + g.Area;
            };

            foreach (var group in groups.Where(g => g.Area <= capacity && g.Preference > 0))
            {
                var preferred = Math.Min(settings.MaxFloors, group.Preference);
                var candidate = preferred;
                while (candidate > 0 && !fits(group, candidate))
                    candidate--;
                if (candidate == 0)
                {
                    candidate = preferred + 1;
                    while (candidate <= settings.MaxFloors && !fits(group, candidate))
                        candidate++;
                }
                if (candidate > settings.MaxFloors)
                    throw new InvalidOperationException("Pinned rooms exceed MaxFloors.");
                place(group, candidate);
                if (candidate != preferred)
                    warnings.Add(group.Rooms[0].Uid + " moved from preferred floor " +
                                 preferred + " to " + candidate + ".");
            }

            foreach (var group in groups.Where(g => g.Area <= capacity && !groupFloor.ContainsKey(g)))
            {
                var bestFloor = 0;
                var bestScore = double.NegativeInfinity;
                for (var floor = 1; floor <= settings.MaxFloors; floor++)
                {
                    if (!fits(group, floor))
                        continue;
                    var floorRooms = groupFloor.Where(kv => kv.Value == floor)
                        .SelectMany(kv => kv.Key.Rooms).ToList();
                    var score = FloorAffinity(group, floorRooms);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestFloor = floor;
                    }
                    if (floorRooms.Count == 0 && bestFloor > 0)
                        break;
                }
                if (bestFloor == 0)
                    throw new InvalidOperationException("Rooms exceed MaxFloors.");
                place(group, bestFloor);
            }

            var nextFloor = used.Keys.DefaultIfEmpty(0).Max();
            foreach (var group in groups.Where(g => g.Area > capacity))
            {
                nextFloor++;
                if (nextFloor > settings.MaxFloors)
                    throw new InvalidOperationException("Oversized rooms exceed MaxFloors.");
                place(group, nextFloor);
                warnings.Add(group.Rooms[0].Uid + " exceeds plate capacity and occupies floor " +
                             nextFloor + ".");
            }

            var answer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in groupFloor)
                foreach (var room in pair.Key.Rooms)
                    answer[room.Uid] = pair.Value;
            return answer;
        }

        private static double FloorAffinity(Group group, IList<BubbleRoom> floorRooms)
        {
            if (floorRooms.Count == 0)
                return group.Area * 1e-9;
            var floorIds = new HashSet<string>(floorRooms.Select(r => r.Uid),
                StringComparer.OrdinalIgnoreCase);
            var ties = group.Rooms.Count(r =>
                !string.IsNullOrWhiteSpace(r.AdjacentTo) && floorIds.Contains(r.AdjacentTo));
            var gx = group.Rooms.Average(r => r.BubbleCenter.X);
            var gy = group.Rooms.Average(r => r.BubbleCenter.Y);
            var fx = floorRooms.Average(r => r.BubbleCenter.X);
            var fy = floorRooms.Average(r => r.BubbleCenter.Y);
            var proximity = 1.0 / (1.0 + Math.Sqrt((gx - fx) * (gx - fx) + (gy - fy) * (gy - fy)));
            var departments = new HashSet<string>(floorRooms.Select(r => r.Department),
                StringComparer.OrdinalIgnoreCase);
            var sameDepartment = group.Rooms.Count(r => departments.Contains(r.Department));
            return 2.5 * ties + proximity + sameDepartment;
        }

        private static Dictionary<string, int> Cluster(
            IList<BubbleRoom> rooms, GenerationSettings settings, int seed)
        {
            var count = Math.Max(1, Math.Min(settings.ClusterCount, rooms.Count));
            var minX = rooms.Min(r => r.BubbleCenter.X);
            var maxX = rooms.Max(r => r.BubbleCenter.X);
            var minY = rooms.Min(r => r.BubbleCenter.Y);
            var maxY = rooms.Max(r => r.BubbleCenter.Y);
            var spanX = Math.Max(1e-9, maxX - minX);
            var spanY = Math.Max(1e-9, maxY - minY);
            var departments = rooms.Select(r => r.Department)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
            var departmentIndex = departments.Select((value, index) => new { value, index })
                .ToDictionary(x => x.value, x => x.index, StringComparer.OrdinalIgnoreCase);
            var byUid = rooms.ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);

            var features = rooms.Select(r =>
            {
                BubbleRoom adjacent = null;
                var hasAdjacent = !string.IsNullOrWhiteSpace(r.AdjacentTo) &&
                                  byUid.TryGetValue(r.AdjacentTo, out adjacent);
                var ax = hasAdjacent ? adjacent.BubbleCenter.X : r.BubbleCenter.X;
                var ay = hasAdjacent ? adjacent.BubbleCenter.Y : r.BubbleCenter.Y;
                return new[]
                {
                    settings.ProximityWeight * (r.BubbleCenter.X - minX) / spanX,
                    settings.ProximityWeight * (r.BubbleCenter.Y - minY) / spanY,
                    settings.AdjacencyWeight * (ax - minX) / spanX,
                    settings.AdjacencyWeight * (ay - minY) / spanY,
                    settings.DepartmentWeight * departmentIndex[r.Department]
                };
            }).ToList();

            var random = new Random(seed);
            var centroids = new List<double[]> { (double[])features[random.Next(features.Count)].Clone() };
            while (centroids.Count < count)
            {
                var farthest = features.OrderByDescending(f => centroids.Min(c => DistanceSquared(f, c)))
                    .First();
                centroids.Add((double[])farthest.Clone());
            }

            var labels = new int[rooms.Count];
            for (var iteration = 0; iteration < 50; iteration++)
            {
                var changed = false;
                for (var i = 0; i < features.Count; i++)
                {
                    var best = Enumerable.Range(0, count)
                        .OrderBy(c => DistanceSquared(features[i], centroids[c])).First();
                    if (labels[i] != best)
                    {
                        labels[i] = best;
                        changed = true;
                    }
                }
                for (var c = 0; c < count; c++)
                {
                    var members = Enumerable.Range(0, features.Count).Where(i => labels[i] == c).ToList();
                    if (members.Count == 0)
                    {
                        var far = 0;
                        var farD = -1.0;
                        for (var i = 0; i < features.Count; i++)
                        {
                            var d = DistanceSquared(features[i], centroids[labels[i]]);
                            if (d > farD)
                            {
                                farD = d;
                                far = i;
                            }
                        }
                        centroids[c] = (double[])features[far].Clone();
                        continue;
                    }
                    for (var d = 0; d < features[0].Length; d++)
                        centroids[c][d] = members.Average(i => features[i][d]);
                }
                if (!changed && iteration > 0)
                    break;
            }

            var compact = labels.Distinct().OrderBy(x => x)
                .Select((value, index) => new { value, index }).ToDictionary(x => x.value, x => x.index);
            var clusterByUid = rooms.Select((room, index) => new { room.Uid, Cluster = compact[labels[index]] })
                .ToDictionary(x => x.Uid, x => x.Cluster, StringComparer.OrdinalIgnoreCase);
            GlueSharedClusters(rooms, clusterByUid);
            return clusterByUid;
        }

        private static void GlueSharedClusters(
            IList<BubbleRoom> rooms, IDictionary<string, int> clusterByUid)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var room in rooms)
                {
                    int parentCluster;
                    int childCluster;
                    if (!room.Shared || string.IsNullOrWhiteSpace(room.AdjacentTo) ||
                        !clusterByUid.TryGetValue(room.AdjacentTo, out parentCluster) ||
                        !clusterByUid.TryGetValue(room.Uid, out childCluster) ||
                        parentCluster == childCluster)
                        continue;
                    clusterByUid[room.Uid] = parentCluster;
                    changed = true;
                }
            }
        }

        private static double DistanceSquared(double[] a, double[] b)
        {
            var sum = 0.0;
            for (var i = 0; i < a.Length; i++)
            {
                var delta = a[i] - b[i];
                sum += delta * delta;
            }
            return sum;
        }

        private static void OrganizeFloor(
            IList<BubbleRoom> rooms,
            int floor,
            IDictionary<string, int> clusterByUid,
            Point3d floorOrigin,
            GenerationSettings settings,
            FloorplanResult result)
        {
            var layouts = new List<ClusterLayout>();
            foreach (var clusterId in clusterByUid.Values.Distinct().OrderBy(x => x))
            {
                var members = rooms.Where(r => clusterByUid[r.Uid] == clusterId).ToList();
                layouts.Add(BuildCluster(clusterId, members, settings));
            }

            var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(layouts.Count)));
            var maxWidth = layouts.Max(l => l.Width);
            var maxHeight = layouts.Max(l => l.Height);
            var clusterGap = settings.Model(settings.TrunkGap);
            var branchHalf = settings.Model(settings.BranchGap) * 0.5;
            var plate = new Rectangle3d(
                new Plane(floorOrigin, Vector3d.ZAxis),
                settings.Model(settings.PlateWidth),
                settings.Model(settings.PlateLength));
            result.Diagnostics.Add(new DiagnosticCurve
            {
                Floor = floor,
                Kind = "plate",
                Curve = plate.ToNurbsCurve()
            });

            for (var row = 0; row * columns < layouts.Count; row++)
            {
                var rowLayouts = layouts.Skip(row * columns).Take(columns).ToList();
                var rowBaseY = floorOrigin.Y - row * (maxHeight + clusterGap * 2.0);
                var spineY = rowBaseY - maxHeight - clusterGap;
                var rowLeft = floorOrigin.X;
                var rowRight = floorOrigin.X + Math.Max(1, rowLayouts.Count - 1) *
                               (maxWidth + clusterGap) + rowLayouts.Last().Width;
                AddCorridor(result, floor,
                    new Line(new Point3d(rowLeft, spineY, 0), new Point3d(rowRight, spineY, 0)),
                    true, rowLayouts.Sum(l => l.Branches.Sum(b => b.Top.Count + b.Bottom.Count)), settings);

                for (var column = 0; column < rowLayouts.Count; column++)
                {
                    var layout = rowLayouts[column];
                    var origin = new Point3d(
                        floorOrigin.X + column * (maxWidth + clusterGap), rowBaseY, 0);
                    EmitCluster(layout, floor, origin, spineY, branchHalf,
                        clusterByUid, settings, result);
                }
            }
        }

        private static ClusterLayout BuildCluster(
            int clusterId, IList<BubbleRoom> members, GenerationSettings settings)
        {
            var byUid = members.ToDictionary(r => r.Uid, StringComparer.OrdinalIgnoreCase);
            var children = new Dictionary<string, List<BubbleRoom>>(StringComparer.OrdinalIgnoreCase);
            foreach (var room in members.Where(r => r.Shared &&
                     !string.IsNullOrWhiteSpace(r.AdjacentTo) && byUid.ContainsKey(r.AdjacentTo)))
            {
                List<BubbleRoom> list;
                if (!children.TryGetValue(room.AdjacentTo, out list))
                {
                    list = new List<BubbleRoom>();
                    children[room.AdjacentTo] = list;
                }
                list.Add(room);
            }

            var childIds = new HashSet<string>(children.Values.SelectMany(x => x).Select(x => x.Uid),
                StringComparer.OrdinalIgnoreCase);
            var ordered = new List<BubbleRoom>();
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<BubbleRoom> emit = null;
            emit = delegate(BubbleRoom room)
            {
                if (!emitted.Add(room.Uid))
                    return;
                ordered.Add(room);
                List<BubbleRoom> list;
                if (children.TryGetValue(room.Uid, out list))
                    foreach (var child in list.OrderBy(r => r.BubbleCenter.X))
                        emit(child);
            };
            foreach (var root in members.Where(r => !childIds.Contains(r.Uid))
                .OrderBy(r => r.BubbleCenter.X).ThenBy(r => r.BubbleCenter.Y))
                emit(root);
            foreach (var room in members.OrderBy(r => r.BubbleCenter.X))
                emit(room);

            var shaped = ordered.Select(r => Shape(r, settings)).ToList();
            var groups = new List<List<ShapedRoom>>();
            for (var index = 0; index < shaped.Count;)
            {
                var group = new List<ShapedRoom> { shaped[index] };
                var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { shaped[index].Room.Uid };
                var next = index + 1;
                while (next < shaped.Count && shaped[next].Room.Shared &&
                       groupIds.Contains(shaped[next].Room.AdjacentTo))
                {
                    group.Add(shaped[next]);
                    groupIds.Add(shaped[next].Room.Uid);
                    next++;
                }
                groups.Add(group);
                index = next;
            }

            var rows = new List<List<ShapedRoom>>();
            var maxRun = settings.Model(settings.MaxCorridorLength);
            var roomGap = settings.Model(settings.RoomGap);
            var current = new List<ShapedRoom>();
            var currentWidth = 0.0;
            foreach (var group in groups)
            {
                var groupWidth = group.Sum(r => r.Width) + roomGap * Math.Max(0, group.Count - 1);
                if (current.Count > 0 && currentWidth + roomGap + groupWidth > maxRun)
                {
                    rows.Add(current);
                    current = new List<ShapedRoom>();
                    currentWidth = 0;
                }
                if (current.Count > 0)
                    currentWidth += roomGap;
                current.AddRange(group);
                currentWidth += groupWidth;
            }
            if (current.Count > 0)
                rows.Add(current);

            var layout = new ClusterLayout { Id = clusterId };
            for (var i = 0; i < rows.Count; i += 2)
            {
                var branch = new BranchLayout();
                branch.Top.AddRange(rows[i]);
                if (i + 1 < rows.Count)
                    branch.Bottom.AddRange(rows[i + 1]);
                branch.Width = Math.Max(RowWidth(branch.Top, roomGap), RowWidth(branch.Bottom, roomGap));
                layout.Branches.Add(branch);
            }
            layout.Width = layout.Branches.Max(b => b.Width);
            layout.Height = layout.Branches.Sum(b =>
                MaxDepth(b.Top) + settings.Model(settings.BranchGap) + MaxDepth(b.Bottom)) +
                settings.Model(settings.TrunkGap) * Math.Max(0, layout.Branches.Count - 1);
            return layout;
        }

        private static ShapedRoom Shape(BubbleRoom room, GenerationSettings settings)
        {
            var area = room.Area * settings.UnitScale * settings.UnitScale;
            var depth = settings.Model(settings.RoomDepth);
            var width = area / depth;
            var aspect = Math.Max(width, depth) / Math.Max(1e-9, Math.Min(width, depth));
            if (aspect > settings.MaxAspect)
            {
                if (width > depth)
                {
                    depth = Math.Sqrt(area / settings.MaxAspect);
                    width = area / depth;
                }
                else
                {
                    width = Math.Sqrt(area / settings.MaxAspect);
                    depth = area / width;
                }
            }
            return new ShapedRoom { Room = room, Width = width, Depth = depth };
        }

        private static double RowWidth(IList<ShapedRoom> row, double gap)
        {
            return row.Count == 0 ? 0 : row.Sum(r => r.Width) + gap * (row.Count - 1);
        }

        private static double MaxDepth(IList<ShapedRoom> row)
        {
            return row.Count == 0 ? 0 : row.Max(r => r.Depth);
        }

        private static void EmitCluster(
            ClusterLayout layout,
            int floor,
            Point3d origin,
            double spineY,
            double branchHalf,
            IDictionary<string, int> clusterByUid,
            GenerationSettings settings,
            FloorplanResult result)
        {
            var yCursor = origin.Y;
            var connectorX = origin.X + layout.Width * 0.5;
            var roomGap = settings.Model(settings.RoomGap);
            var branchCenters = new List<double>();

            foreach (var branch in layout.Branches)
            {
                var topDepth = MaxDepth(branch.Top);
                var bottomDepth = MaxDepth(branch.Bottom);
                var branchY = yCursor - topDepth - branchHalf;
                branchCenters.Add(branchY);
                EmitRow(branch.Top, floor, layout.Id, origin.X,
                    branchY + branchHalf, true, roomGap, result);
                EmitRow(branch.Bottom, floor, layout.Id, origin.X,
                    branchY - branchHalf, false, roomGap, result);
                AddCorridor(result, floor,
                    new Line(new Point3d(origin.X, branchY, 0),
                             new Point3d(origin.X + branch.Width, branchY, 0)),
                    false, branch.Top.Count + branch.Bottom.Count, settings);
                yCursor = branchY - branchHalf - bottomDepth - settings.Model(settings.TrunkGap);
            }

            if (branchCenters.Count > 0)
            {
                var farthest = branchCenters.OrderByDescending(y => Math.Abs(y - spineY)).First();
                AddCorridor(result, floor,
                    new Line(new Point3d(connectorX, spineY, 0),
                             new Point3d(connectorX, farthest, 0)),
                    true, layout.Branches.Sum(b => b.Top.Count + b.Bottom.Count), settings);
            }

            var clusterRooms = result.Rooms.Where(r => r.Floor == floor && r.Cluster == layout.Id).ToList();
            if (clusterRooms.Count > 0)
            {
                var minX = clusterRooms.Min(r => r.Rectangle.BoundingBox.Min.X);
                var minY = clusterRooms.Min(r => r.Rectangle.BoundingBox.Min.Y);
                var maxX = clusterRooms.Max(r => r.Rectangle.BoundingBox.Max.X);
                var maxY = clusterRooms.Max(r => r.Rectangle.BoundingBox.Max.Y);
                var clusterRect = new Rectangle3d(
                    new Plane(new Point3d(minX, minY, 0), Vector3d.ZAxis),
                    Math.Max(1e-6, maxX - minX),
                    Math.Max(1e-6, maxY - minY));
                result.Diagnostics.Add(new DiagnosticCurve
                {
                    Floor = floor,
                    Kind = "cluster",
                    Curve = clusterRect.ToNurbsCurve()
                });
            }
        }

        private static void EmitRow(
            IList<ShapedRoom> row,
            int floor,
            int cluster,
            double startX,
            double accessY,
            bool growsUp,
            double gap,
            FloorplanResult result)
        {
            var x = startX;
            ShapedRoom previous = null;
            foreach (var shaped in row)
            {
                if (previous != null && shaped.Room.Shared &&
                    string.Equals(shaped.Room.AdjacentTo, previous.Room.Uid,
                        StringComparison.OrdinalIgnoreCase))
                    x -= gap;
                var y = growsUp ? accessY : accessY - shaped.Depth;
                var plane = new Plane(new Point3d(x, y, 0), Vector3d.ZAxis);
                result.Rooms.Add(new RoomPlacement
                {
                    Room = shaped.Room,
                    Floor = floor,
                    Cluster = cluster,
                    Rectangle = new Rectangle3d(plane, shaped.Width, shaped.Depth),
                    AccessEdge = growsUp ? 3 : 1
                });
                x += shaped.Width + gap;
                previous = shaped;
            }
        }

        private static void AddCorridor(
            FloorplanResult result,
            int floor,
            Line line,
            bool isTrunk,
            int served,
            GenerationSettings settings)
        {
            if (line.Length <= 1e-8)
                return;
            var width = isTrunk
                ? settings.TrunkRibbonWidth(served)
                : settings.Model(settings.BranchWidth);
            result.Corridors.Add(new CorridorSegment
            {
                Floor = floor,
                Line = line,
                IsTrunk = isTrunk,
                RoomsServed = served,
                Width = width
            });
        }
    }
}
