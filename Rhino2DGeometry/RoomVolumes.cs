using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    // UI-independent settings: a future Eto panel can populate this object.
    public sealed class RoomVolumeSettings
    {
        public double DefaultRoomHeightFeet { get; set; } = 14.0;
        public IDictionary<string, double> HeightOverridesFeet { get; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public double ResolveHeightFeet(string uid)
        {
            double height;
            if (!HeightOverridesFeet.TryGetValue(uid, out height)) height = DefaultRoomHeightFeet;
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
                throw new ArgumentException("Room height must be finite and positive: " + uid);
            return height;
        }

        internal void Validate()
        {
            if (double.IsNaN(DefaultRoomHeightFeet) || double.IsInfinity(DefaultRoomHeightFeet) || DefaultRoomHeightFeet <= 0)
                throw new ArgumentException("Default room height must be finite and positive.");
            foreach (var uid in HeightOverridesFeet.Keys) ResolveHeightFeet(uid);
        }
    }

    internal static class RoomVolumes
    {
        public static Extrusion Create(RoomPlacement room, GenerationSettings settings, RoomVolumeSettings volumes)
        {
            if (room.Room.RoomType == RoomType.Staircase) return null;
            var height = settings.Model(volumes.ResolveHeightFeet(room.Room.Uid));
            using (var footprint = room.Rectangle.ToNurbsCurve())
            {
                var solid = Extrusion.Create(footprint, height, true);
                if (solid == null || !solid.IsValid || !solid.IsSolid)
                    throw new InvalidOperationException("Could not create room volume: " + room.Room.Uid);
                return solid;
            }
        }
    }
}
