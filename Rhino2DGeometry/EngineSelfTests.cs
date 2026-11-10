using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal static class EngineSelfTests
    {
        public static string Run()
        {
            RunManaged();
            RunGeometry();
            return "Test2DFloorplanEngine passed: metadata, clustering, exact areas, " +
                   "shared-wall doors, corridor widths, wall openings, door swings, room volumes, and SVG export.";
        }

        public static string RunManaged()
        {
            TestMetadataParsing();
            TestMetadataValidation();
            TestCorridorWidthFormula();
            TestLayoutAnalysis();
            TestRoomVolumeSettings();
            return "Managed tests passed: metadata, stair classification, height settings, corridor widths, floor assignment, clustering, exact area, and chain packing.";
        }

        private static void TestLayoutAnalysis()
        {
            var settings = TestSettings();
            var rooms = Fixture();
            var first = LayoutEngine.Analyze(rooms, settings);
            var second = LayoutEngine.Analyze(rooms, settings);

            Assert(first.FloorByUid.Count == rooms.Count, "analyzed room count");
            Assert(first.FloorByUid["A"] == first.FloorByUid["A.R"] &&
                   first.FloorByUid["A.R"] == first.FloorByUid["A.V"],
                "shared chain stays on one floor");

            foreach (var room in rooms)
            {
                var area = first.WidthByUid[room.Uid] * first.DepthByUid[room.Uid];
                Assert(Math.Abs(area - room.Area * settings.UnitScale * settings.UnitScale) <= 1e-6,
                    room.Uid + " exact shaped area");
            }

            var packed = first.PackingOrderByCluster.SelectMany(kv => kv.Value).ToList();
            var restroom = packed.IndexOf("A.R");
            var vestibule = packed.IndexOf("A.V");
            var classroom = packed.IndexOf("A");
            Assert(classroom >= 0 && restroom == classroom + 1, "restroom follows classroom");
            Assert(vestibule == restroom + 1, "vestibule follows restroom");

            foreach (var uid in first.ClusterByUid.Keys)
                Assert(first.ClusterByUid[uid] == second.ClusterByUid[uid], uid + " deterministic cluster");
        }

        public static string RunGeometry()
        {
            TestLayoutAndCirculation();
            TestWallOpenings();
            TestVolumesAndSvg();
            return "Geometry tests passed: clustering, exact areas, shared walls, openings, swings, unit-aware volumes, flat stairs, and SVG export.";
        }

        private static void TestRoomVolumeSettings()
        {
            Assert(UserTextParser.ParseRoomType(null, "Stair 2") == RoomType.Staircase, "numbered stair");
            Assert(UserTextParser.ParseRoomType(null, "Stair2") == RoomType.Staircase, "attached stair number");
            Assert(UserTextParser.ParseRoomType(null, "STAIRCASE A") == RoomType.Staircase, "staircase label");
            Assert(UserTextParser.ParseRoomType(null, "Upstairs office") == RoomType.Room, "whole word stair detection");
            Assert(UserTextParser.ParseRoomType("Room", "Stairs") == RoomType.Room, "explicit room override");
            Assert(UserTextParser.ParseRoomType("Staircase", "Core") == RoomType.Staircase, "explicit staircase");
            ExpectError(() => UserTextParser.ParseRoomType("Invalid", "Stairs"), "RoomType");
            Assert(System.IO.Directory.Exists(System.IO.Path.Combine(FloorplanSvg.ResolveProjectRoot(), "Rhino2DGeometry")), "export root from assembly location");
            var settings = new RoomVolumeSettings();
            Assert(settings.ResolveHeightFeet("A") == 14, "default height");
            settings.HeightOverridesFeet["A"] = 18;
            Assert(settings.ResolveHeightFeet("a") == 18, "UID height override");
            foreach (var invalid in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            {
                settings.HeightOverridesFeet["A"] = invalid;
                var rejected = false;
                try { settings.ResolveHeightFeet("A"); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "invalid height rejected");
                settings.DefaultRoomHeightFeet = invalid;
                rejected = false;
                try { settings.ResolveHeightFeet("B"); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "invalid default rejected");
            }
        }

        private static void TestVolumesAndSvg()
        {
            foreach (var scale in new[] { 1.0, 304.8 })
            {
                var settings = TestSettings();
                settings.UnitScale = scale;
                var fixture = Fixture();
                fixture.Last().FloorPreference = 2;
                var result = FloorplanPipeline.Generate(fixture, settings, 0.01);
                Assert(result.FloorCount >= 2, "multi-floor volume fixture");
                result.Rooms[0].Room.Label = "Room <A> & B";
                result.Rooms[1].Room.RoomType = RoomType.Staircase;
                var heights = new RoomVolumeSettings();
                heights.HeightOverridesFeet[result.Rooms[0].Room.Uid] = 18;
                foreach (var room in result.Rooms)
                {
                    using (var solid = RoomVolumes.Create(room, settings, heights))
                    {
                        if (room.Room.RoomType == RoomType.Staircase)
                            Assert(solid == null, "stairs stay flat");
                        else
                        {
                            Assert(solid.IsSolid, "capped room solid");
                            var bounds = solid.GetBoundingBox(true);
                            Assert(Math.Abs(bounds.Max.Z - bounds.Min.Z - heights.ResolveHeightFeet(room.Room.Uid) * scale) < 1e-6, "height in model units");
                            using (var brep = solid.ToBrep())
                            using (var mass = VolumeMassProperties.Compute(brep))
                                Assert(Math.Abs(mass.Volume - room.Room.Area * scale * scale * heights.ResolveHeightFeet(room.Room.Uid) * scale) < Math.Max(1e-4, mass.Volume * 1e-8), "room volume preserves footprint area");
                            Assert(bounds.Min.DistanceTo(room.Rectangle.BoundingBox.Min) < 1e-6, "room base position preserved");
                        }
                    }
                }
                Assert(result.Corridors.All(c => Math.Abs(c.Line.From.Z) < 1e-8 && Math.Abs(c.Line.To.Z) < 1e-8), "corridors planar");
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "floorplan-test-" + Guid.NewGuid() + ".svg");
                try
                {
                    FloorplanSvg.Write(path, result, settings, 0.01);
                    var xml = System.Xml.Linq.XDocument.Load(path);
                    System.Xml.Linq.XNamespace ns = "http://www.w3.org/2000/svg";
                    Assert(xml.Descendants(ns + "text").Count() == result.Rooms.Count, "SVG room labels");
                    Assert(xml.Descendants(ns + "tspan").Any(t => t.Value == "Room <A> & B"), "escaped SVG label");
                    Assert(xml.Descendants(ns + "path").Any(p => ((string)p.Attribute("d")).Contains(" A ")), "SVG door arcs");
                    Assert(xml.Root.Attribute("viewBox") != null, "SVG bounds");
                    Assert(xml.Descendants(ns + "g").Count(g => g.Attribute("id") != null) == result.FloorCount, "SVG all floors");
                    Assert(xml.Descendants(ns + "polyline").Count() == result.Corridors.Count(c => c.Ribbon != null), "SVG corridor ribbons");
                    var view = ((string)xml.Root.Attribute("viewBox")).Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    Assert(view[2] > 0 && view[3] > 0, "positive SVG bounds");
                    foreach (var room in result.Rooms)
                    {
                        var b = room.Rectangle.BoundingBox;
                        Assert(b.Min.X >= view[0] && b.Max.X <= view[0] + view[2] && -b.Max.Y >= view[1] && -b.Min.Y <= view[1] + view[3], "SVG contains room footprint");
                    }
                    var center = result.Rooms[0].Rectangle.Center;
                    var text = xml.Descendants(ns + "text").First();
                    Assert(Math.Abs(double.Parse((string)text.Attribute("y"), System.Globalization.CultureInfo.InvariantCulture) + center.Y) < 1e-6, "SVG Y reflection");
                    result.Rooms[0].Room.Label = "Replacement";
                    FloorplanSvg.Write(path, result, settings, 0.01);
                    Assert(!System.IO.File.ReadAllText(path).Contains("&lt;A&gt;"), "SVG replaces previous generation");
                }
                finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            }
        }

        public static string RunBake(RhinoDoc doc)
        {
            if (doc == null)
                throw new ArgumentNullException("doc");
            TestMixedBubbleSelection(doc);
            var settings = TestSettings();
            var fixture = Fixture();
            fixture.Last().RoomType = RoomType.Staircase;
            var result = FloorplanPipeline.Generate(fixture, settings, Math.Max(0.01, doc.ModelAbsoluteTolerance));
            var beforeIds = new HashSet<Guid>(doc.Objects.Select(o => o.Id));
            var added = DocumentWriter.Write(doc, result, settings);
            Assert(added > 0, "baked object count");
            Assert(doc.Objects.Count >= added, "document objects");
            var paths = doc.Layers.Select(l => l.FullPath).ToList();
            Assert(paths.Any(p => p.IndexOf("Rooms", StringComparison.OrdinalIgnoreCase) >= 0), "Rooms layer");
            Assert(paths.Any(p => p.IndexOf("Branch", StringComparison.OrdinalIgnoreCase) >= 0), "Branch layer");
            Assert(paths.Any(p => p.IndexOf("Trunk", StringComparison.OrdinalIgnoreCase) >= 0), "Trunk layer");
            Assert(paths.Any(p => p.IndexOf("Doors", StringComparison.OrdinalIgnoreCase) >= 0), "Doors layer");
            Assert(paths.Any(p => p.IndexOf("Diagnostics", StringComparison.OrdinalIgnoreCase) >= 0), "Diagnostics layer");
            Assert(doc.Groups.Count >= result.FloorCount, "floor groups");
            var created = doc.Objects.Where(o => !beforeIds.Contains(o.Id)).ToList();
            var volumes = created.Where(o => doc.Layers[o.Attributes.LayerIndex].Name == "Rooms3D").ToList();
            Assert(volumes.Count == result.Rooms.Count(r => r.Room.RoomType == RoomType.Room), "baked volume count");
            Assert(volumes.All(o => o.Attributes.GetUserString("HeightFeet") == "14" && o.Attributes.GetUserString("RoomType") == "Room"), "volume metadata");
            var stairs = created.Where(o => doc.Layers[o.Attributes.LayerIndex].Name == "Stairs").ToList();
            Assert(stairs.Count == 1 && ((Curve)stairs[0].Geometry).IsClosed && stairs[0].Geometry.GetBoundingBox(true).Max.Z == 0, "baked flat closed staircase");
            return "Bake test passed: " + added + " objects on " + result.FloorCount + " floor(s).";
        }

        private static void TestMixedBubbleSelection(RhinoDoc doc)
        {
            var ids = new List<Guid>();
            try
            {
                var room = new Rhino.DocObjects.ObjectAttributes();
                room.SetUserString("UID", "SelectionTest");
                room.SetUserString("Area", "100");
                ids.Add(doc.Objects.AddCircle(new Circle(Plane.WorldXY, 5), room));
                ids.Add(doc.Objects.AddLine(Point3d.Origin, new Point3d(10, 0, 0)));
                ids.Add(doc.Objects.AddCurve(new Rectangle3d(Plane.WorldXY, 20, 20).ToNurbsCurve()));
                ids.Add(doc.Objects.AddCircle(new Circle(Plane.WorldXY, 20)));
                int ignored;
                var rooms = BubbleMetadataReader.Read(ids.Select(id => doc.Objects.FindId(id)), 3, out ignored);
                Assert(rooms.Count == 1 && ignored == 3, "mixed selection ignores untagged connections, boundaries, and other curves");
                ExpectError(() => BubbleMetadataReader.Read(ids.Skip(1).Select(id => doc.Objects.FindId(id)), 3), "No valid");

                var malformed = doc.Objects.AddLine(Point3d.Origin, new Point3d(5, 0, 0), room);
                ids.Add(malformed);
                ExpectError(() => BubbleMetadataReader.Read(new[] { doc.Objects.FindId(malformed) }, 3), "not a circle");
                var missingArea = new Rhino.DocObjects.ObjectAttributes();
                missingArea.SetUserString("UID", "MissingArea");
                var invalidCircle = doc.Objects.AddCircle(new Circle(Plane.WorldXY, 5), missingArea);
                ids.Add(invalidCircle);
                ExpectError(() => BubbleMetadataReader.Read(new[] { doc.Objects.FindId(invalidCircle) }, 3), "Area");
            }
            finally
            {
                foreach (var id in ids) doc.Objects.Delete(id, true);
            }
        }

        private static void TestMetadataParsing()
        {
            var room = UserTextParser.Parse(
                new Dictionary<string, string>
                {
                    { "UID", " 1.1#0 " },
                    { "Area", "850" },
                    { "Label", "Classroom K" },
                    { "Dept", "Academic" },
                    { "FloorPref", "2" },
                    { "Shared", "true" },
                    { "AdjacentTo", "1.1.1#0" },
                    { "DoorWidth", "3.5" }
                },
                new Point3d(10, 4, 7),
                12.5,
                Color.SteelBlue,
                Guid.NewGuid(),
                3.0);

            Assert(room.Uid == "1.1#0", "UID trim");
            Assert(room.Area == 850, "Area parse");
            Assert(room.Label == "Classroom K", "Label");
            Assert(room.Department == "Academic", "Dept");
            Assert(room.FloorPreference == 2, "FloorPref");
            Assert(room.Shared, "Shared true");
            Assert(room.AdjacentTo == "1.1.1#0", "AdjacentTo");
            Assert(Math.Abs(room.DoorWidth - 3.5) < 1e-9, "DoorWidth");
            Assert(Math.Abs(room.BubbleCenter.Z) < 1e-12, "planar center");

            var defaults = UserTextParser.Parse(
                new Dictionary<string, string> { { "UID", "X" }, { "Area", "100" } },
                Point3d.Origin, 1, Color.Gray, Guid.Empty, 3.0);
            Assert(defaults.Label == "X", "default label");
            Assert(defaults.Department == "Unassigned", "default dept");
            Assert(defaults.FloorPreference == 0, "default floor pref");
            Assert(!defaults.Shared, "default shared");
            Assert(defaults.AdjacentTo == string.Empty, "default adjacency");
            Assert(Math.Abs(defaults.DoorWidth - 3.0) < 1e-9, "default door width");
        }

        private static void TestMetadataValidation()
        {
            ExpectError(() => UserTextParser.Parse(
                new Dictionary<string, string> { { "Area", "10" } },
                Point3d.Origin, 1, Color.Gray, Guid.Empty, 3), "UID");
            ExpectError(() => UserTextParser.Parse(
                new Dictionary<string, string> { { "UID", "A" }, { "Area", "0" } },
                Point3d.Origin, 1, Color.Gray, Guid.Empty, 3), "Area");

            var duplicate = new List<BubbleRoom>
            {
                Room("A", 100, 0, 0, "", false, "A"),
                Room("A", 120, 5, 0, "", false, "A")
            };
            ExpectError(() => UserTextParser.Validate(duplicate), "Duplicate");

            var unresolved = new List<BubbleRoom>
            {
                Room("A", 100, 0, 0, "missing", false, "A")
            };
            ExpectError(() => UserTextParser.Validate(unresolved), "Unresolved");
        }

        private static void TestLayoutAndCirculation()
        {
            var settings = TestSettings();
            var rooms = Fixture();
            UserTextParser.Validate(rooms);
            var first = FloorplanPipeline.Generate(rooms, settings, 0.01);
            var second = FloorplanPipeline.Generate(rooms, settings, 0.01);

            Assert(first.Rooms.Count == rooms.Count, "room count");
            Assert(first.Rooms.Select(r => r.Room.Uid).Distinct().Count() == rooms.Count,
                "unique UIDs");
            Assert(first.Doors.Count == rooms.Count, "one door per room");
            Assert(first.Doors.Count(d => d.IsDirect) == 2, "shared direct doors");
            Assert(first.Corridors.Count > 0, "corridor generation");
            Assert(first.Corridors.Any(c => !c.IsTrunk), "branch corridors");
            Assert(first.Corridors.Any(c => c.IsTrunk), "trunk corridors");
            Assert(first.Diagnostics.Any(d => d.Kind == "plate"), "plate diagnostic");
            Assert(ConnectedCenterlines(first.Corridors), "connected corridor centerlines");

            foreach (var corridor in first.Corridors)
            {
                if (corridor.IsTrunk)
                {
                    Assert(Math.Abs(corridor.Width - settings.TrunkRibbonWidth(corridor.RoomsServed)) < 1e-9,
                        "trunk width formula");
                    Assert(corridor.Width <= settings.Model(settings.TrunkMaxWidth) + 1e-9,
                        "trunk cap");
                }
                else
                    Assert(Math.Abs(corridor.Width - settings.Model(settings.BranchWidth)) < 1e-9,
                        "branch width");
                Assert(corridor.Ribbon != null && corridor.Ribbon.IsClosed, "closed ribbon");
            }

            foreach (var placement in first.Rooms)
            {
                var area = placement.Rectangle.Width * placement.Rectangle.Height;
                Assert(Math.Abs(area - placement.Room.Area * settings.UnitScale * settings.UnitScale) <= 1e-6,
                    placement.Room.Uid + " exact area");
            }

            for (var i = 0; i < first.Rooms.Count; i++)
            {
                Assert(first.Rooms[i].Room.Uid == second.Rooms[i].Room.Uid, "deterministic ordering");
                Assert(first.Rooms[i].Rectangle.Center.DistanceTo(
                    second.Rooms[i].Rectangle.Center) <= 1e-8, "deterministic placement");
            }

            var byUid = first.Rooms.ToDictionary(r => r.Room.Uid, StringComparer.OrdinalIgnoreCase);
            AssertSharedWall(byUid["A.R"], byUid["A"], 1.0);
            AssertSharedWall(byUid["A.V"], byUid["A.R"], 1.0);

            foreach (var door in first.Doors)
            {
                var room = byUid[door.Room.Uid];
                Assert(Math.Abs(door.Width - 3.0) <= 1e-6 || door.Width < 3.0 + 1e-6,
                    door.Room.Uid + " door clamped");
                Assert(door.Swing.IsValid, door.Room.Uid + " swing arc");
                Assert(Math.Abs(door.Swing.AngleDegrees - 90) < 1.0,
                    door.Room.Uid + " 90-degree swing");
                var box = room.Rectangle.BoundingBox;
                var inward = door.OpenEnd.X > box.Min.X + 1e-6 &&
                             door.OpenEnd.X < box.Max.X - 1e-6 &&
                             door.OpenEnd.Y > box.Min.Y + 1e-6 &&
                             door.OpenEnd.Y < box.Max.Y - 1e-6;
                Assert(inward, door.Room.Uid + " swing opens inward");
            }
        }

        private static void TestCorridorWidthFormula()
        {
            var settings = new GenerationSettings
            {
                UnitScale = 1,
                TrunkBaseWidth = 3,
                TrunkStep = 0.5,
                TrunkThreshold = 4,
                TrunkMaxWidth = 8
            };
            Assert(Math.Abs(settings.TrunkRibbonWidth(2) - 3.0) < 1e-9, "below threshold");
            Assert(Math.Abs(settings.TrunkRibbonWidth(4) - 3.0) < 1e-9, "at threshold");
            Assert(Math.Abs(settings.TrunkRibbonWidth(6) - 4.0) < 1e-9, "two extra rooms");
            Assert(Math.Abs(settings.TrunkRibbonWidth(40) - 8.0) < 1e-9, "cap");
        }

        private static void TestWallOpenings()
        {
            var room = new RoomPlacement
            {
                Room = Room("W", 100, 0, 0, "", false, "A"),
                Floor = 1,
                Rectangle = new Rectangle3d(Plane.WorldXY, 10, 10),
                AccessEdge = 1
            };
            var door = new DoorPlacement
            {
                Room = room.Room,
                Floor = 1,
                Edge = 1,
                Width = 3,
                Hinge = new Point3d(3.5, 10, 0),
                ClosedEnd = new Point3d(6.5, 10, 0),
                OpenEnd = new Point3d(3.5, 7, 0)
            };
            var segments = WallOpenings.Segments(room, new[] { door }, 0.01);
            var top = segments.Where(s => Math.Abs(s.From.Y - 10) < 1e-8 &&
                                          Math.Abs(s.To.Y - 10) < 1e-8).ToList();
            Assert(top.Count == 2, "opening splits the wall");
            Assert(Math.Abs(top.Sum(s => s.Length) - 7.0) < 1e-6, "remaining wall length");
        }

        private static bool ConnectedCenterlines(IList<CorridorSegment> corridors)
        {
            if (corridors.Count == 0)
                return false;
            var remaining = Enumerable.Range(1, corridors.Count - 1).ToList();
            var queue = new Queue<int>();
            queue.Enqueue(0);
            var seen = new HashSet<int> { 0 };
            while (queue.Count > 0)
            {
                var current = corridors[queue.Dequeue()];
                for (var i = remaining.Count - 1; i >= 0; i--)
                {
                    var other = corridors[remaining[i]];
                    if (!SegmentsMeet(current.Line, other.Line, 1e-3))
                        continue;
                    if (seen.Add(remaining[i]))
                        queue.Enqueue(remaining[i]);
                    remaining.RemoveAt(i);
                }
            }
            return remaining.Count == 0;
        }

        private static bool SegmentsMeet(Line a, Line b, double tolerance)
        {
            if (a.From.DistanceTo(b.From) <= tolerance || a.From.DistanceTo(b.To) <= tolerance ||
                a.To.DistanceTo(b.From) <= tolerance || a.To.DistanceTo(b.To) <= tolerance)
                return true;
            return PointOnSegment(a.From, b, tolerance) || PointOnSegment(a.To, b, tolerance) ||
                   PointOnSegment(b.From, a, tolerance) || PointOnSegment(b.To, a, tolerance);
        }

        private static bool PointOnSegment(Point3d point, Line line, double tolerance)
        {
            var closest = line.ClosestPoint(point, true);
            return point.DistanceTo(closest) <= tolerance;
        }

        private static void AssertSharedWall(RoomPlacement child, RoomPlacement parent, double tolerance)
        {
            var a = child.Rectangle.BoundingBox;
            var b = parent.Rectangle.BoundingBox;
            var vertical = (Math.Abs(a.Max.X - b.Min.X) <= tolerance ||
                            Math.Abs(a.Min.X - b.Max.X) <= tolerance) &&
                           Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y) > 1e-6;
            var horizontal = (Math.Abs(a.Max.Y - b.Min.Y) <= tolerance ||
                              Math.Abs(a.Min.Y - b.Max.Y) <= tolerance) &&
                             Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X) > 1e-6;
            Assert(vertical || horizontal, child.Room.Uid + " shares a wall with " + parent.Room.Uid);
        }

        private static GenerationSettings TestSettings()
        {
            return new GenerationSettings
            {
                UnitScale = 1.0,
                PlateWidth = 200,
                PlateLength = 150,
                FloorEfficiency = 0.7,
                ClusterCount = 2,
                RoomGap = 0.5,
                BranchGap = 6,
                TrunkGap = 8,
                BranchWidth = 3,
                TrunkBaseWidth = 3,
                TrunkStep = 0.5,
                TrunkThreshold = 2,
                TrunkMaxWidth = 10,
                Seed = 7
            };
        }

        private static List<BubbleRoom> Fixture()
        {
            return new List<BubbleRoom>
            {
                Room("A", 850, 0, 0, "", false, "Academic"),
                Room("A.R", 80, 12, 0, "A", true, "Support"),
                Room("A.V", 60, 16, 0, "A.R", true, "Support"),
                Room("B", 850, 35, 2, "A", false, "Academic"),
                Room("C", 1200, 65, 8, "B", false, "Science"),
                Room("D", 600, 12, 35, "A", false, "Admin")
            };
        }

        private static BubbleRoom Room(string uid, double area, double x, double y,
            string adjacent, bool shared, string department)
        {
            return new BubbleRoom
            {
                Uid = uid,
                Label = uid,
                Area = area,
                BubbleCenter = new Point3d(x, y, 0),
                BubbleRadius = Math.Sqrt(area / Math.PI),
                AdjacentTo = adjacent,
                Shared = shared,
                Department = department,
                DoorWidth = 3,
                FloorPreference = 1,
                Color = Color.SteelBlue
            };
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Assertion failed: " + name);
        }

        private static void ExpectError(Action action, string fragment)
        {
            try
            {
                action();
            }
            catch (ArgumentException exception)
            {
                Assert(exception.Message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0,
                    "error mentions " + fragment);
                return;
            }
            throw new InvalidOperationException("Expected an error containing '" + fragment + "'.");
        }
    }
}
