using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal static class UserTextParser
    {
        public static BubbleRoom Parse(
            IDictionary<string, string> text,
            Point3d center,
            double radius,
            Color color,
            Guid sourceId,
            double defaultDoorWidth)
        {
            if (text == null)
                throw new ArgumentNullException("text");

            var uid = Value(text, "UID");
            if (string.IsNullOrWhiteSpace(uid))
                throw new ArgumentException("required user text 'UID' is missing.");

            double area;
            if (!TryDouble(Value(text, "Area"), out area) || area <= 0)
                throw new ArgumentException(uid + ": required user text 'Area' must be positive.");

            int floorPreference;
            if (!int.TryParse(Value(text, "FloorPref"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out floorPreference) || floorPreference < 0)
                floorPreference = 0;

            double doorWidth;
            if (!TryDouble(Value(text, "DoorWidth"), out doorWidth) || doorWidth <= 0)
                doorWidth = defaultDoorWidth;

            center.Z = 0;
            return new BubbleRoom
            {
                SourceId = sourceId,
                Uid = uid.Trim(),
                Label = Fallback(Value(text, "Label"), uid.Trim()),
                Department = Fallback(Value(text, "Dept"), "Unassigned"),
                Area = area,
                FloorPreference = floorPreference,
                Shared = ParseBool(Value(text, "Shared")),
                AdjacentTo = Fallback(Value(text, "AdjacentTo"), string.Empty),
                DoorWidth = doorWidth,
                BubbleCenter = center,
                BubbleRadius = radius,
                Color = color
            };
        }

        public static void Validate(IList<BubbleRoom> rooms)
        {
            var errors = new List<string>();
            var duplicates = rooms.GroupBy(r => r.Uid, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
                errors.Add("Duplicate UIDs: " + string.Join(", ", duplicates));

            var ids = new HashSet<string>(rooms.Select(r => r.Uid), StringComparer.OrdinalIgnoreCase);
            var unresolved = rooms.Where(r => !string.IsNullOrWhiteSpace(r.AdjacentTo) &&
                !ids.Contains(r.AdjacentTo)).Select(r => r.Uid + " -> " + r.AdjacentTo).ToList();
            if (unresolved.Count > 0)
                errors.Add("Unresolved AdjacentTo references: " + string.Join(", ", unresolved));

            if (errors.Count > 0)
                throw new ArgumentException("Bubble metadata is invalid:\n  " + string.Join("\n  ", errors));
            if (rooms.Count == 0)
                throw new ArgumentException("No valid metadata-tagged circles were selected.");
        }

        public static bool TryDouble(string text, out double value)
        {
            return double.TryParse((text ?? string.Empty).Replace(",", string.Empty),
                       NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        public static bool ParseBool(string text)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value == "1";
        }

        private static string Value(IDictionary<string, string> text, string key)
        {
            string value;
            return text.TryGetValue(key, out value) ? value : null;
        }

        private static string Fallback(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}
