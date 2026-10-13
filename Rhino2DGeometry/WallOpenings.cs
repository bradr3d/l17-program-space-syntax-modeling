using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal static class WallOpenings
    {
        public static List<Line> Segments(
            RoomPlacement room, IEnumerable<DoorPlacement> doors, double tolerance)
        {
            var result = new List<Line>();
            var box = room.Rectangle.BoundingBox;
            var doorList = doors == null ? new List<DoorPlacement>() : doors.ToList();
            for (var edge = 0; edge < 4; edge++)
            {
                var line = EdgeLine(box, edge);
                var intervals = new List<Interval> { new Interval(0, 1) };
                foreach (var door in doorList)
                {
                    if (!Affects(room, door))
                        continue;
                    var center = 0.5 * (door.Hinge + door.ClosedEnd);
                    var parameter = line.ClosestParameter(center);
                    var point = line.PointAt(parameter);
                    if (parameter < -tolerance || parameter > 1 + tolerance ||
                        point.DistanceTo(center) > Math.Max(tolerance * 4, 1e-6))
                        continue;
                    var halfParameter = door.Width * 0.5 / Math.Max(line.Length, 1e-9);
                    intervals = Subtract(intervals,
                        new Interval(parameter - halfParameter, parameter + halfParameter));
                }
                foreach (var interval in intervals)
                    if (interval.Length * line.Length > tolerance)
                        result.Add(new Line(line.PointAt(interval.T0), line.PointAt(interval.T1)));
            }
            return result;
        }

        public static Line EdgeLine(BoundingBox box, int edge)
        {
            if (edge == 0)
                return new Line(new Point3d(box.Max.X, box.Min.Y, 0), new Point3d(box.Max.X, box.Max.Y, 0));
            if (edge == 1)
                return new Line(new Point3d(box.Min.X, box.Max.Y, 0), new Point3d(box.Max.X, box.Max.Y, 0));
            if (edge == 2)
                return new Line(new Point3d(box.Min.X, box.Min.Y, 0), new Point3d(box.Min.X, box.Max.Y, 0));
            return new Line(new Point3d(box.Min.X, box.Min.Y, 0), new Point3d(box.Max.X, box.Min.Y, 0));
        }

        private static bool Affects(RoomPlacement room, DoorPlacement door)
        {
            return door.Room.Uid.Equals(room.Room.Uid, StringComparison.OrdinalIgnoreCase) ||
                   door.IsDirect;
        }

        private static List<Interval> Subtract(IList<Interval> source, Interval cut)
        {
            var result = new List<Interval>();
            foreach (var interval in source)
            {
                var low = Math.Max(interval.T0, cut.T0);
                var high = Math.Min(interval.T1, cut.T1);
                if (high <= low)
                {
                    result.Add(interval);
                    continue;
                }
                if (low > interval.T0)
                    result.Add(new Interval(interval.T0, low));
                if (high < interval.T1)
                    result.Add(new Interval(high, interval.T1));
            }
            return result;
        }
    }
}
