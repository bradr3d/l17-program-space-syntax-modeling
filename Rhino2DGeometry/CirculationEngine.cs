using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal static class CirculationEngine
    {
        public static void Build(FloorplanResult result, GenerationSettings settings, double tolerance)
        {
            BuildCorridorRibbons(result);
            BuildDoors(result, settings, tolerance);
        }

        private static void BuildCorridorRibbons(FloorplanResult result)
        {
            foreach (var corridor in result.Corridors)
            {
                var direction = corridor.Line.Direction;
                if (!direction.Unitize())
                    continue;
                var perpendicular = new Vector3d(-direction.Y, direction.X, 0);
                var half = corridor.Width * 0.5;
                var a = corridor.Line.From + perpendicular * half;
                var b = corridor.Line.To + perpendicular * half;
                var c = corridor.Line.To - perpendicular * half;
                var d = corridor.Line.From - perpendicular * half;
                corridor.Ribbon = new Polyline(new[] { a, b, c, d, a }).ToNurbsCurve();
            }
        }

        private static void BuildDoors(
            FloorplanResult result, GenerationSettings settings, double tolerance)
        {
            var byFloor = result.Rooms.GroupBy(r => r.Floor);
            foreach (var floorGroup in byFloor)
            {
                var rooms = floorGroup.ToList();
                var byUid = rooms.ToDictionary(r => r.Room.Uid, StringComparer.OrdinalIgnoreCase);
                var corridors = result.Corridors.Where(c => c.Floor == floorGroup.Key).ToList();
                var floorCenter = AverageCenter(rooms);

                foreach (var room in rooms)
                {
                    int edge;
                    Point3d center;
                    var direct = false;
                    RoomPlacement parent;
                    if (room.Room.Shared &&
                        !string.IsNullOrWhiteSpace(room.Room.AdjacentTo) &&
                        byUid.TryGetValue(room.Room.AdjacentTo, out parent) &&
                        TrySharedWall(room.Rectangle.BoundingBox, parent.Rectangle.BoundingBox,
                            Math.Max(tolerance, settings.Model(1.0)), out edge, out center))
                    {
                        direct = true;
                    }
                    else
                    {
                        edge = ChooseCorridorEdge(room, corridors, floorCenter);
                        center = EdgeMidpoint(room.Rectangle.BoundingBox, edge);
                        if (room.Room.Shared)
                            result.Warnings.Add(room.Room.Uid +
                                " has no shared wall with its parent; corridor door used.");
                    }

                    var available = EdgeLength(room.Rectangle.BoundingBox, edge);
                    var requested = settings.Model(room.Room.DoorWidth);
                    var width = Math.Min(requested, Math.Max(settings.Model(1.5),
                        available - Math.Max(tolerance * 4.0, settings.Model(0.5))));
                    if (width <= tolerance)
                    {
                        result.Warnings.Add(room.Room.Uid + " wall is too short for a door.");
                        continue;
                    }
                    result.Doors.Add(CreateDoor(room.Room, floorGroup.Key, direct,
                        edge, center, width));
                }
            }
        }

        private static DoorPlacement CreateDoor(
            BubbleRoom room, int floor, bool direct, int edge, Point3d center, double width)
        {
            Vector3d wall;
            Vector3d inward;
            if (edge == 0)
            {
                wall = Vector3d.YAxis;
                inward = -Vector3d.XAxis;
            }
            else if (edge == 1)
            {
                wall = Vector3d.XAxis;
                inward = -Vector3d.YAxis;
            }
            else if (edge == 2)
            {
                wall = Vector3d.YAxis;
                inward = Vector3d.XAxis;
            }
            else
            {
                wall = Vector3d.XAxis;
                inward = Vector3d.YAxis;
            }

            var hinge = center - wall * (width * 0.5);
            var closed = center + wall * (width * 0.5);
            var open = hinge + inward * width;
            var bisector = wall + inward;
            bisector.Unitize();
            var mid = hinge + bisector * width;
            return new DoorPlacement
            {
                Room = room,
                Floor = floor,
                IsDirect = direct,
                Edge = edge,
                Hinge = hinge,
                ClosedEnd = closed,
                OpenEnd = open,
                Width = width,
                Swing = new Arc(closed, mid, open)
            };
        }

        private static int ChooseCorridorEdge(
            RoomPlacement room, IList<CorridorSegment> corridors, Point3d floorCenter)
        {
            var box = room.Rectangle.BoundingBox;
            var bestEdge = room.AccessEdge;
            var bestDistance = double.MaxValue;
            var bestInterior = double.MaxValue;
            for (var edge = 0; edge < 4; edge++)
            {
                var midpoint = EdgeMidpoint(box, edge);
                var distance = corridors.Count == 0
                    ? double.MaxValue
                    : corridors.Min(c => DistanceToSegment(midpoint, c.Line));
                var interior = midpoint.DistanceTo(floorCenter);
                if (distance < bestDistance - 1e-7 ||
                    (Math.Abs(distance - bestDistance) <= 1e-7 && interior < bestInterior))
                {
                    bestEdge = edge;
                    bestDistance = distance;
                    bestInterior = interior;
                }
            }
            return bestEdge;
        }

        private static double DistanceToSegment(Point3d point, Line line)
        {
            var parameter = line.ClosestParameter(point);
            parameter = Math.Max(0.0, Math.Min(1.0, parameter));
            return point.DistanceTo(line.PointAt(parameter));
        }

        private static Point3d AverageCenter(IList<RoomPlacement> rooms)
        {
            return new Point3d(
                rooms.Average(r => r.Rectangle.Center.X),
                rooms.Average(r => r.Rectangle.Center.Y), 0);
        }

        internal static Point3d EdgeMidpoint(BoundingBox box, int edge)
        {
            var cx = (box.Min.X + box.Max.X) * 0.5;
            var cy = (box.Min.Y + box.Max.Y) * 0.5;
            if (edge == 0) return new Point3d(box.Max.X, cy, 0);
            if (edge == 1) return new Point3d(cx, box.Max.Y, 0);
            if (edge == 2) return new Point3d(box.Min.X, cy, 0);
            return new Point3d(cx, box.Min.Y, 0);
        }

        private static double EdgeLength(BoundingBox box, int edge)
        {
            return edge == 0 || edge == 2
                ? box.Max.Y - box.Min.Y
                : box.Max.X - box.Min.X;
        }

        private static bool TrySharedWall(
            BoundingBox room,
            BoundingBox parent,
            double tolerance,
            out int edge,
            out Point3d center)
        {
            double low;
            double high;
            if (Math.Abs(room.Max.X - parent.Min.X) <= tolerance &&
                Overlap(room.Min.Y, room.Max.Y, parent.Min.Y, parent.Max.Y, out low, out high))
            {
                edge = 0; center = new Point3d(room.Max.X, (low + high) * 0.5, 0); return true;
            }
            if (Math.Abs(room.Min.X - parent.Max.X) <= tolerance &&
                Overlap(room.Min.Y, room.Max.Y, parent.Min.Y, parent.Max.Y, out low, out high))
            {
                edge = 2; center = new Point3d(room.Min.X, (low + high) * 0.5, 0); return true;
            }
            if (Math.Abs(room.Max.Y - parent.Min.Y) <= tolerance &&
                Overlap(room.Min.X, room.Max.X, parent.Min.X, parent.Max.X, out low, out high))
            {
                edge = 1; center = new Point3d((low + high) * 0.5, room.Max.Y, 0); return true;
            }
            if (Math.Abs(room.Min.Y - parent.Max.Y) <= tolerance &&
                Overlap(room.Min.X, room.Max.X, parent.Min.X, parent.Max.X, out low, out high))
            {
                edge = 3; center = new Point3d((low + high) * 0.5, room.Min.Y, 0); return true;
            }
            edge = -1;
            center = Point3d.Unset;
            return false;
        }

        private static bool Overlap(
            double a0, double a1, double b0, double b1, out double low, out double high)
        {
            low = Math.Max(a0, b0);
            high = Math.Min(a1, b1);
            return high - low > 1e-7;
        }
    }
}
