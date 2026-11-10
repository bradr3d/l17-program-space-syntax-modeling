using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace ExcelToBubbleDiagram
{
    internal static class ComponentBubbleLayout
    {
        public static List<FloorLayout> Solve(
            BubbleDiagramData data, BubbleDiagramOptions options, double feetToModel)
        {
            if (!double.IsFinite(feetToModel) || feetToModel <= 0)
                throw new ArgumentException("The document unit scale must be finite and positive.", nameof(feetToModel));
            if (!double.IsFinite(options.BoundaryScale) || options.BoundaryScale < 1)
                throw new ArgumentException("The diagram frame scale must be at least one.", nameof(options));

            var result = BubbleSolverAdapter.Solve(data, options);
            var solvedByUid = result.Rooms.ToDictionary(room => room.Uid, StringComparer.Ordinal);
            var metersToModel = feetToModel / BubbleSolverAdapter.MetersPerFoot;
            var layouts = new List<FloorLayout>();
            var nextOffset = 0.0;
            foreach (var floor in data.FloorTotals())
            {
                var layout = new FloorLayout { Floor = floor.Floor };
                layout.Nodes.AddRange(data.Nodes.Where(node =>
                    string.Equals(node.Source.Floor, floor.Floor, StringComparison.OrdinalIgnoreCase)));
                var indices = layout.Nodes.Select(node => node.Index).ToHashSet();
                layout.Connections.UnionWith(data.Connections.Where(pair => indices.Contains(pair.A) && indices.Contains(pair.B)));

                foreach (var node in layout.Nodes)
                {
                    var room = solvedByUid[node.Uid];
                    node.Position = new Point3d(room.X * metersToModel, room.Y * metersToModel, 0);
                    node.Radius = room.Radius * metersToModel;
                }

                var floorId = solvedByUid[layout.Nodes[0].Uid].Floor;
                var envelope = result.FloorEnvelopes.Single(item => item.Floor == floorId);
                var xMargin = envelope.Width * (options.BoundaryScale - 1) * 0.5;
                var yMargin = envelope.Height * (options.BoundaryScale - 1) * 0.5;
                var frame = new Rectangle3d(Plane.WorldXY,
                    new Interval((envelope.MinX - xMargin) * metersToModel, (envelope.MaxX + xMargin) * metersToModel),
                    new Interval((envelope.MinY - yMargin) * metersToModel, (envelope.MaxY + yMargin) * metersToModel));
                layout.Boundary = frame.ToNurbsCurve();
                var bounds = layout.Boundary.GetBoundingBox(true);
                var spacing = Math.Max(bounds.Diagonal.X, bounds.Diagonal.Y) * 0.25;
                layout.DocumentOffset = new Vector3d(0, nextOffset - bounds.Min.Y, 0);
                nextOffset += bounds.Diagonal.Y + spacing;
                layouts.Add(layout);
            }
            return layouts;
        }
    }
}
