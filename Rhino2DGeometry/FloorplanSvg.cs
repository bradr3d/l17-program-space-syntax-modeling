using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal static class FloorplanSvg
    {
        private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";
        private static string N(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
        private static string P(Point3d p) => N(p.X) + "," + N(-p.Y);

        public static string ResolveProjectRoot()
        {
            var assembly = typeof(FloorplanSvg).Assembly.Location;
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(assembly)); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Rhino2DGeometry", "Rhino2DGeometry.csproj")))
                    return directory.FullName;
            throw new DirectoryNotFoundException("Cannot locate Week 1 Build from the plug-in location. Load the plug-in from this project's bin/Release folder.");
        }

        public static void Write(string path, FloorplanResult result, GenerationSettings settings, double tolerance)
        {
            var bounds = BoundingBox.Empty;
            var svg = new XElement(Ns + "svg", new XAttribute("version", "1.1"));
            var stroke = settings.Model(0.08);
            foreach (var floor in result.Rooms.Select(r => r.Floor).Distinct().OrderBy(f => f))
            {
                var group = new XElement(Ns + "g", new XAttribute("id", "floor-" + floor),
                    new XAttribute("fill", "none"), new XAttribute("stroke-width", N(stroke)));
                svg.Add(group);
                foreach (var corridor in result.Corridors.Where(c => c.Floor == floor && c.Ribbon != null))
                {
                    Polyline polyline;
                    if (!corridor.Ribbon.TryGetPolyline(out polyline))
                        throw new InvalidOperationException("Corridor ribbon is not a polyline.");
                    bounds.Union(corridor.Ribbon.GetBoundingBox(true));
                    group.Add(new XElement(Ns + "polyline", new XAttribute("points", string.Join(" ", polyline.Select(P))),
                        new XAttribute("stroke", corridor.IsTrunk ? "#6495ED" : "#4682B4")));
                }
                var doors = result.Doors.Where(d => d.Floor == floor).ToList();
                foreach (var room in result.Rooms.Where(r => r.Floor == floor))
                {
                    bounds.Union(room.Rectangle.BoundingBox);
                    var color = "#" + room.Room.Color.R.ToString("X2") + room.Room.Color.G.ToString("X2") + room.Room.Color.B.ToString("X2");
                    var roomGroup = new XElement(Ns + "g", new XAttribute("data-uid", room.Room.Uid),
                        new XAttribute("data-room-type", room.Room.RoomType), new XAttribute("stroke", color));
                    group.Add(roomGroup);
                    if (room.Room.RoomType == RoomType.Staircase)
                        roomGroup.Add(new XElement(Ns + "polygon", new XAttribute("points",
                            string.Join(" ", Enumerable.Range(0, 4).Select(i => P(room.Rectangle.Corner(i)))))));
                    foreach (var wall in WallOpenings.Segments(room, doors, tolerance))
                        roomGroup.Add(Line(wall.From, wall.To));
                    var font = Math.Max(settings.Model(1), Math.Min(room.Rectangle.Width, room.Rectangle.Height) * 0.08);
                    var center = room.Rectangle.Center;
                    var label = new XElement(Ns + "text", new XAttribute("x", N(center.X)), new XAttribute("y", N(-center.Y)),
                        new XAttribute("text-anchor", "middle"), new XAttribute("font-family", "sans-serif"),
                        new XAttribute("font-size", N(font)), new XAttribute("fill", "black"), new XAttribute("stroke", "none"),
                        new XElement(Ns + "tspan", new XAttribute("x", N(center.X)), room.Room.Label),
                        new XElement(Ns + "tspan", new XAttribute("x", N(center.X)), new XAttribute("dy", N(font * 1.2)),
                            N(room.Room.Area) + " SF"));
                    roomGroup.Add(label);
                    // Conservative text bounds prevent long labels from being clipped.
                    var halfWidth = Math.Max(room.Room.Label.Length, (N(room.Room.Area) + " SF").Length) * font;
                    bounds.Union(new Point3d(center.X - halfWidth, center.Y - font * 2, 0));
                    bounds.Union(new Point3d(center.X + halfWidth, center.Y + font * 2, 0));
                }
                foreach (var door in doors)
                {
                    var leaf = Line(door.Hinge, door.OpenEnd);
                    leaf.SetAttributeValue("stroke", "#8B4513");
                    group.Add(leaf);
                    var arc = door.Swing;
                    bounds.Union(new BoundingBox(new[] { door.Hinge, door.OpenEnd }));
                    using (var curve = arc.ToNurbsCurve()) bounds.Union(curve.GetBoundingBox(true));
                    group.Add(new XElement(Ns + "path", new XAttribute("stroke", "#8B4513"),
                        new XAttribute("d", "M " + P(arc.StartPoint) + " A " + N(arc.Radius) + " " + N(arc.Radius) +
                        " 0 " + (arc.Angle > Math.PI ? "1" : "0") + " " + (arc.Plane.ZAxis.Z > 0 ? "0" : "1") + " " + P(arc.EndPoint))));
                }
            }
            if (!bounds.IsValid) throw new InvalidOperationException("No floorplan geometry to export.");
            var margin = settings.Model(2);
            svg.SetAttributeValue("viewBox", N(bounds.Min.X - margin) + " " + N(-bounds.Max.Y - margin) + " " +
                N(bounds.Max.X - bounds.Min.X + margin * 2) + " " + N(bounds.Max.Y - bounds.Min.Y + margin * 2));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                new XDocument(svg).Save(temporary);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static XElement Line(Point3d start, Point3d end) => new XElement(Ns + "path",
            new XAttribute("d", "M " + P(start) + " L " + P(end)));
    }
}
