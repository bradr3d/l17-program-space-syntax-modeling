using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal static class DocumentWriter
    {
        public static int Write(
            RhinoDoc doc, FloorplanResult result, GenerationSettings settings, RoomVolumeSettings volumes = null)
        {
            volumes = volumes ?? new RoomVolumeSettings();
            volumes.Validate();
            var root = EnsureLayer(doc, "Floorplan", Guid.Empty, Color.DimGray);
            var added = 0;

            foreach (var floor in result.Rooms.Select(r => r.Floor).Distinct().OrderBy(f => f))
            {
                var floorLayer = EnsureLayer(doc, "Floor " + floor,
                    doc.Layers[root].Id, Color.Gray);
                var parentId = doc.Layers[floorLayer].Id;
                var roomLayer = EnsureLayer(doc, "Rooms", parentId, Color.DarkSlateGray);
                var volumeLayer = EnsureLayer(doc, "Rooms3D", parentId, Color.DarkSlateGray);
                var stairsLayer = EnsureLayer(doc, "Stairs", parentId, Color.Gray);
                var labelLayer = EnsureLayer(doc, "Labels", parentId, Color.Black);
                var corridorParent = EnsureLayer(doc, "Corridors", parentId, Color.Gray);
                var corridorParentId = doc.Layers[corridorParent].Id;
                var branchLayer = EnsureLayer(doc, "Branch", corridorParentId, Color.SteelBlue);
                var trunkLayer = EnsureLayer(doc, "Trunk", corridorParentId, Color.CornflowerBlue);
                var doorLayer = EnsureLayer(doc, "Doors", parentId, Color.SaddleBrown);
                var diagnosticsLayer = EnsureLayer(doc, "Diagnostics", parentId, Color.Orange);
                var floorIds = new List<Guid>();

                foreach (var corridor in result.Corridors.Where(c => c.Floor == floor))
                {
                    if (corridor.Ribbon == null)
                        continue;
                    var attributes = Attributes(
                        corridor.IsTrunk ? trunkLayer : branchLayer,
                        corridor.IsTrunk ? Color.CornflowerBlue : Color.SteelBlue,
                        corridor.IsTrunk ? "Trunk corridor" : "Branch corridor");
                    attributes.SetUserString("Floor", floor.ToString());
                    attributes.SetUserString("Kind", corridor.IsTrunk ? "trunk" : "branch");
                    attributes.SetUserString("Width", (corridor.Width / settings.UnitScale).ToString("0.###"));
                    attributes.SetUserString("RoomsServed", corridor.RoomsServed.ToString());
                    AddId(doc.Objects.AddCurve(corridor.Ribbon, attributes), floorIds, ref added);
                }

                var floorDoors = result.Doors.Where(d => d.Floor == floor).ToList();
                foreach (var room in result.Rooms.Where(r => r.Floor == floor))
                {
                    if (room.Room.RoomType == RoomType.Staircase)
                    {
                        using (var footprint = room.Rectangle.ToNurbsCurve())
                            AddId(doc.Objects.AddCurve(footprint, RoomAttributes(stairsLayer, room)), floorIds, ref added);
                    }
                    else
                    {
                        using (var solid = RoomVolumes.Create(room, settings, volumes))
                        {
                            var volumeAttributes = RoomAttributes(volumeLayer, room);
                            volumeAttributes.SetUserString("HeightFeet", volumes.ResolveHeightFeet(room.Room.Uid).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            var id = doc.Objects.AddExtrusion(solid, volumeAttributes);
                            if (id == Guid.Empty) throw new InvalidOperationException("Failed to bake room volume: " + room.Room.Uid);
                            AddId(id, floorIds, ref added);
                        }
                    }
                    var attributes = RoomAttributes(roomLayer, room);
                    foreach (var wall in WallOpenings.Segments(room, floorDoors,
                        doc.ModelAbsoluteTolerance))
                        AddId(doc.Objects.AddLine(wall, attributes), floorIds, ref added);

                    var label = new TextEntity
                    {
                        Plane = new Plane(room.Rectangle.Center, Vector3d.ZAxis),
                        PlainText = room.Room.Label + "\n" + room.Room.Area.ToString("0.#") + " SF",
                        TextHeight = Math.Max(settings.Model(1.0),
                            Math.Min(room.Rectangle.Width, room.Rectangle.Height) * 0.08),
                        Justification = TextJustification.MiddleCenter
                    };
                    var labelAttributes = RoomAttributes(labelLayer, room);
                    AddId(doc.Objects.AddText(label, labelAttributes), floorIds, ref added);
                }

                foreach (var door in floorDoors)
                {
                    var attributes = Attributes(doorLayer, Color.SaddleBrown,
                        door.Room.Uid + (door.IsDirect ? " direct door" : " corridor door"));
                    attributes.SetUserString("UID", door.Room.Uid);
                    attributes.SetUserString("Floor", floor.ToString());
                    attributes.SetUserString("DoorKind", door.IsDirect ? "direct" : "corridor");
                    attributes.SetUserString("DoorWidth",
                        (door.Width / settings.UnitScale).ToString("0.###"));
                    AddId(doc.Objects.AddLine(door.Hinge, door.OpenEnd, attributes),
                        floorIds, ref added);
                    AddId(doc.Objects.AddArc(door.Swing, attributes), floorIds, ref added);
                }

                foreach (var diagnostic in result.Diagnostics.Where(d => d.Floor == floor))
                {
                    if (diagnostic.Curve == null)
                        continue;
                    var attributes = Attributes(diagnosticsLayer, Color.Orange, diagnostic.Kind);
                    attributes.PlotWeight = 0.13;
                    attributes.SetUserString("Floor", floor.ToString());
                    attributes.SetUserString("Kind", diagnostic.Kind);
                    AddId(doc.Objects.AddCurve(diagnostic.Curve, attributes), floorIds, ref added);
                }

                if (floorIds.Count > 0)
                    doc.Groups.Add("Floorplan Floor " + floor, floorIds);
            }
            if (doc.Views != null)
                doc.Views.Redraw();
            return added;
        }

        private static ObjectAttributes RoomAttributes(int layer, RoomPlacement room)
        {
            var attributes = Attributes(layer, room.Room.Color, room.Room.Label);
            attributes.SetUserString("UID", room.Room.Uid);
            attributes.SetUserString("RoomType", room.Room.RoomType.ToString());
            attributes.SetUserString("Area", room.Room.Area.ToString("0.###"));
            attributes.SetUserString("Dept", room.Room.Department);
            attributes.SetUserString("Floor", room.Floor.ToString());
            attributes.SetUserString("Cluster", room.Cluster.ToString());
            attributes.SetUserString("Shared", room.Room.Shared.ToString());
            attributes.SetUserString("AdjacentTo", room.Room.AdjacentTo ?? string.Empty);
            return attributes;
        }

        private static ObjectAttributes Attributes(int layer, Color color, string name)
        {
            return new ObjectAttributes
            {
                LayerIndex = layer,
                ColorSource = ObjectColorSource.ColorFromObject,
                ObjectColor = color,
                Name = name
            };
        }

        private static void AddId(Guid id, IList<Guid> ids, ref int count)
        {
            if (id == Guid.Empty)
                return;
            ids.Add(id);
            count++;
        }

        private static int EnsureLayer(
            RhinoDoc document, string name, Guid parentId, Color color)
        {
            foreach (var layer in document.Layers)
                if (!layer.IsDeleted && layer.ParentLayerId == parentId &&
                    layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return layer.Index;

            var created = new Layer { Name = name, ParentLayerId = parentId, Color = color };
            var index = document.Layers.Add(created);
            if (index < 0)
                throw new InvalidOperationException("Could not create layer '" + name + "'.");
            return index;
        }
    }
}
