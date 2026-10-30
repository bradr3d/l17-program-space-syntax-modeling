// Adapter: bubble solver output -> Psm.Viz Scene.
// Write one of these for every solver (tiler, corridors, ...).

using System.Globalization;
using BubbleSolver;

namespace Psm.Viz.Cli;

/// <summary>
/// Where each floor is drawn (side by side).
/// </summary>
public sealed class FloorLayout
{
    public Dictionary<int, double> OffsetX { get; } = new();

    public static FloorLayout From(IEnumerable<IReadOnlyList<BubbleResult>> snapshots)
    {
        SortedDictionary<int, (double Min, double Max)> ext = new();

        foreach (IReadOnlyList<BubbleResult> snap in snapshots)
        {
            foreach (BubbleResult r in snap)
            {
                (double Min, double Max) e = ext.TryGetValue(r.Floor, out (double Min, double Max) v) ? v : (double.MaxValue, double.MinValue);
                ext[r.Floor] = (Math.Min(e.Min, r.X - r.Radius), Math.Max(e.Max, r.X + r.Radius));
            }
        }

        FloorLayout layout = new();
        double widest = ext.Values.Select(v => v.Max - v.Min).DefaultIfEmpty(1).Max();
        double cursor = 0;

        foreach (KeyValuePair<int, (double Min, double Max)> kv in ext)
        {
            layout.OffsetX[kv.Key] = cursor - kv.Value.Min;
            cursor += (kv.Value.Max - kv.Value.Min) + 0.12 * widest;
        }

        return layout;
    }
}

public static class BubbleScenes
{
    private const double FtPerM = 1 / 0.3048;

    public static Scene Build(
        IReadOnlyList<BubbleResult> rooms,
        LoadedProgram program,
        FloorLayout layout,
        double padModel,
        string title,
        bool showLegend = true)
    {
        Dictionary<string, RoomInfo> info = program.Rooms.ToDictionary(r => r.Uid);
        Dictionary<string, BubbleResult> byUid = rooms.ToDictionary(r => r.Uid);
        double Ox(BubbleResult r) => layout.OffsetX.TryGetValue(r.Floor, out double o) ? o : 0;

        Scene scene = new()
        {
            Title = title
        };

        Layer envLayer = scene["Floor envelopes"];
        Layer bubbleLayer = scene["Bubbles"];
        Layer adjLayer = scene["Adjacency"];
        Layer stretchLayer = scene["Stretched adjacency"];
        Layer overlapLayer = scene["Overlaps"];
        Layer labelLayer = scene["Labels"];
        Layer legendLayer = scene["Legend"];

        double medianR = rooms.Select(r => r.Radius).OrderBy(x => x).ElementAtOrDefault(rooms.Count / 2);
        if (medianR <= 0)
        {
            medianR = 1;
        }

        // ---- floors: envelope + caption -------------------------------------
        Style envStyle = new(Fill: "#f7f6f2", Stroke: "#9a978c", StrokeWidth: 1, Dash: "6 4");
        Style capStyle = new(TextColor: "#555");
        double top = rooms.Count > 0 ? rooms.Max(r => r.Y + r.Radius) : 0;

        foreach (IGrouping<int, BubbleResult> grp in rooms.GroupBy(r => r.Floor).OrderBy(g => g.Key))
        {
            double ox = Ox(grp.First());
            double minX = grp.Min(r => r.X - r.Radius) + ox;
            double maxX = grp.Max(r => r.X + r.Radius) + ox;
            double minY = grp.Min(r => r.Y - r.Radius);
            double maxY = grp.Max(r => r.Y + r.Radius);
            double sf = grp.Sum(r => info.TryGetValue(r.Uid, out RoomInfo? i) ? i.NetSf : 0);

            envLayer.Add(new Rect(minX, minY, maxX - minX, maxY - minY) { Style = envStyle });
            labelLayer.Add(new Label(minX, top + medianR * 1.1,
                $"Floor {grp.Key}  ·  {grp.Count()} rooms  ·  {sf.ToString("#,##0", CultureInfo.InvariantCulture)} SF",
                medianR * 0.7, Anchor: "start", Bold: true)
            { Style = capStyle });
        }

        // ---- bubbles ---------------------------------------------------------
        foreach (BubbleResult r in rooms)
        {
            info.TryGetValue(r.Uid, out RoomInfo? ri);
            string fill = ri?.Color ?? "#999999";

            bubbleLayer.Add(new Circle(r.X + Ox(r), r.Y, r.Radius)
            {
                Style = new Style(Fill: fill, Stroke: Style.Darken(fill), StrokeWidth: 1, FillOpacity: 0.8),
                Tooltip = $"{r.Label}\n{ri?.NetSf.ToString("#,##0", CultureInfo.InvariantCulture)} SF target  ·  floor {r.Floor}  ·  {r.Uid}",
            });
        }

        // ---- adjacency (same floor only; cross-floor pulls act "as if stacked") ----
        Style adjacent = new(Stroke: "#333", StrokeWidth: 1, StrokeOpacity: 0.45, Dash: "4 3");
        Style shared = new(Stroke: "#222", StrokeWidth: 2, StrokeOpacity: 0.8);
        Style stretched = new(Stroke: "#e0661b", StrokeWidth: 2.5, StrokeOpacity: 0.9);

        foreach (ProgramEdge e in program.Edges)
        {
            if (!byUid.TryGetValue(e.From, out BubbleResult? a)
                || !byUid.TryGetValue(e.To, out BubbleResult? b)
                || a.Floor != b.Floor)
            {
                continue;
            }

            double d = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            double gap = d - (a.Radius + b.Radius + padModel);   // > 0: not touching
            bool isStretched = gap > 0.5 * (a.Radius + b.Radius);

            Line line = new(a.X + Ox(a), a.Y, b.X + Ox(b), b.Y)
            {
                Style = e.SharedWall ? shared : adjacent,
                Tooltip = $"{a.Uid} → {b.Uid}  ({(e.SharedWall ? "shared wall" : "adjacent")})\ngap {Math.Max(0, gap) * FtPerM:0.0} ft",
            };

            adjLayer.Add(line);

            if (isStretched)
            {
                stretchLayer.Add(line with { Style = stretched });
            }
        }

        // ---- overlaps (true geometric overlap, ignoring padding) -------------
        Style overlapStyle = new(Fill: "#d62728", FillOpacity: 0.25, Stroke: "#d62728", StrokeWidth: 2);

        foreach (IGrouping<int, BubbleResult> grp in rooms.GroupBy(r => r.Floor))
        {
            List<BubbleResult> m = grp.ToList();

            for (int i = 0; i < m.Count; i++)
            {
                for (int j = i + 1; j < m.Count; j++)
                {
                    BubbleResult a = m[i];
                    BubbleResult b = m[j];
                    double d = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                    double ov = a.Radius + b.Radius - d;

                    if (ov <= 0.01)
                    {
                        continue;
                    }

                    // small disc at the center of the lens between the two circles
                    double t = d > 1e-9 ? (a.Radius - ov / 2) / d : 0;
                    double cx = a.X + (b.X - a.X) * t + Ox(a);
                    double cy = a.Y + (b.Y - a.Y) * t;

                    overlapLayer.Add(new Circle(cx, cy, Math.Max(ov / 2, medianR * 0.12))
                    {
                        Style = overlapStyle,
                        Tooltip = $"{a.Uid} overlaps {b.Uid} by {ov * FtPerM:0.00} ft",
                    });
                }
            }
        }

        // ---- labels ----------------------------------------------------------
        Style textDark = new(TextColor: "#111");
        Style textSoft = new(TextColor: "#333");

        foreach (BubbleResult r in rooms)
        {
            if (!info.TryGetValue(r.Uid, out RoomInfo? ri))
            {
                continue;
            }

            double size = r.Radius * 0.42;
            double x = r.X + Ox(r);
            double nameSize = r.Radius * 0.26;
            bool nameFits = ri.Name.Length * nameSize * 0.55 < r.Radius * 1.7;   // name too, if it fits on one line

            labelLayer.Add(new Label(x, nameFits ? r.Y + size * 0.35 : r.Y, ri.TypeId, size, Bold: true)
            { Style = textDark, Tooltip = r.Label });

            if (nameFits)
            {
                labelLayer.Add(new Label(x, r.Y - size * 0.75, ri.Name, nameSize) { Style = textSoft });
            }
        }

        // ---- legend ----------------------------------------------------------
        if (showLegend && rooms.Count > 0)
        {
            double lx = rooms.Max(r => r.X + r.Radius + Ox(r)) + medianR * 2;
            double ly = top - medianR * 0.5;
            double sw = medianR * 0.8;

            legendLayer.Add(new Label(lx, ly + sw * 1.4, "Departments", sw * 0.7, Anchor: "start", Bold: true) { Style = capStyle });

            IEnumerable<IGrouping<string, RoomInfo>> depts = program.Rooms
                .GroupBy(r => r.DepartmentId)
                .OrderBy(g => int.TryParse(g.Key, out int k) ? k : int.MaxValue);

            foreach (IGrouping<string, RoomInfo> dg in depts)
            {
                string fill = dg.First().Color;
                legendLayer.Add(new Rect(lx, ly - sw, sw, sw) { Style = new Style(Fill: fill, Stroke: Style.Darken(fill), FillOpacity: 0.8) });
                program.Departments.TryGetValue(dg.Key, out string? name);
                legendLayer.Add(new Label(lx + sw * 1.4, ly - sw / 2, $"{dg.Key}  {name}  ({dg.Count()})", sw * 0.6, Anchor: "start") { Style = textSoft });
                ly -= sw * 1.5;
            }

            ly -= sw * 0.6;

            (Shape Swatch, string Text)[] keys =
            {
                (new Line(lx, 0, lx + sw * 2, 0) { Style = shared }, "shared wall"),
                (new Line(lx, 0, lx + sw * 2, 0) { Style = adjacent }, "adjacent"),
                (new Line(lx, 0, lx + sw * 2, 0) { Style = stretched }, "stretched (gap > ½ combined radius)"),
                (new Circle(lx + sw, 0, sw * 0.35) { Style = overlapStyle }, "overlap"),
            };

            foreach ((Shape swatch, string text) in keys)
            {
                legendLayer.Add(swatch switch
                {
                    Line l => l with { Y1 = ly, Y2 = ly },
                    Circle c => c with { Y = ly },
                    _ => swatch,
                });

                legendLayer.Add(new Label(lx + sw * 2.6, ly, text, sw * 0.6, Anchor: "start") { Style = textSoft });
                ly -= sw * 1.2;
            }
        }

        scene.Notes.Add($"rooms {rooms.Count}   stretched edges {stretchLayer.Shapes.Count}   overlaps {overlapLayer.Shapes.Count}");

        return scene;
    }
}