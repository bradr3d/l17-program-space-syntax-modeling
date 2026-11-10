using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal static class BubbleMetadataReader
    {
        public static List<BubbleRoom> Read(IEnumerable<RhinoObject> objects, double defaultDoorWidth)
        {
            int ignoredCount;
            return Read(objects, defaultDoorWidth, out ignoredCount);
        }

        public static List<BubbleRoom> Read(IEnumerable<RhinoObject> objects, double defaultDoorWidth, out int ignoredCount)
        {
            var rooms = new List<BubbleRoom>();
            var errors = new List<string>();
            ignoredCount = 0;

            foreach (var obj in objects)
            {
                // Window selections include diagram connections, labels, and the boundary.
                // Only objects carrying room identity/area metadata are room candidates.
                if (!HasRoomMetadata(obj.Attributes))
                {
                    ignoredCount++;
                    continue;
                }
                Circle circle;
                var curve = obj.Geometry as Curve;
                if (curve == null || !curve.TryGetCircle(out circle))
                {
                    errors.Add(Describe(obj) + ": object is not a circle.");
                    continue;
                }

                try
                {
                    rooms.Add(UserTextParser.Parse(
                        ReadUserText(obj),
                        circle.Center,
                        circle.Radius,
                        ResolveColor(obj),
                        obj.Id,
                        defaultDoorWidth));
                }
                catch (ArgumentException exception)
                {
                    errors.Add(Describe(obj) + ": " + exception.Message);
                }
            }

            if (errors.Count > 0)
                throw new ArgumentException("Bubble metadata is invalid:\n  " + string.Join("\n  ", errors));

            UserTextParser.Validate(rooms);
            return rooms;
        }

        internal static bool HasRoomMetadata(ObjectAttributes attributes) =>
            attributes.GetUserString("UID") != null || attributes.GetUserString("Area") != null;

        private static Dictionary<string, string> ReadUserText(RhinoObject obj)
        {
            var keys = new[] { "UID", "Area", "Label", "Dept", "FloorPref", "Shared", "AdjacentTo", "DoorWidth", "RoomType" };
            var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
                text[key] = obj.Attributes.GetUserString(key);
            return text;
        }

        private static Color ResolveColor(RhinoObject obj)
        {
            if (obj.Attributes.ColorSource == ObjectColorSource.ColorFromObject)
                return obj.Attributes.ObjectColor;
            var layer = obj.Document == null ? null : obj.Document.Layers[obj.Attributes.LayerIndex];
            return layer == null ? Color.SteelBlue : layer.Color;
        }

        private static string Describe(RhinoObject obj)
        {
            return string.IsNullOrWhiteSpace(obj.Attributes.Name)
                ? obj.Id.ToString()
                : obj.Attributes.Name;
        }
    }
}
