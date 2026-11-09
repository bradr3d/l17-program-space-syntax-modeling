// A tiny 2D scene model that any solver can emit.
// Coordinates are world units with +Y up.

namespace Psm.Viz;

/// <summary>
/// Paint for a shape.
/// Colors are CSS colors ("#a8ae49", "none").
/// StrokeWidth is in screen pixels.
/// </summary>
/// <param name="Fill"></param>
/// <param name="Stroke"></param>
/// <param name="StrokeWidth"></param>
/// <param name="FillOpacity"></param>
/// <param name="StrokeOpacity"></param>
/// <param name="Dash"></param>
/// <param name="TextColor"></param>
public sealed record Style(
    string Fill = "none",
    string Stroke = "#222",
    double StrokeWidth = 1,
    double FillOpacity = 1,
    double StrokeOpacity = 1,
    string? Dash = null,
    string? TextColor = null)
{
    public static string Rgb(byte r, byte g, byte b) => $"#{r:x2}{g:x2}{b:x2}";

    /// <summary>A darker shade of a #rrggbb color, for outlines.</summary>
    public static string Darken(string hex, double factor = 0.6)
    {
        if (hex.Length != 7 || hex[0] != '#')
        {
            return hex;
        }

        int C(int i) => (int)(Convert.ToInt32(hex.Substring(i, 2), 16) * factor);

        return $"#{C(1):x2}{C(3):x2}{C(5):x2}";
    }
}

public abstract record Shape
{
    public Style Style { get; init; } = new();
    public string? Tooltip { get; init; }      // hover text in the browser
}

public sealed record Circle(double X, double Y, double R) : Shape;
public sealed record Rect(double X, double Y, double W, double H) : Shape;   // from min corner
public sealed record Polyline(IReadOnlyList<(double X, double Y)> Points, bool Closed = false) : Shape;
public sealed record Line(double X1, double Y1, double X2, double Y2, bool Arrow = false) : Shape;

/// <summary>
/// Text at X,Y. Size is in world units.
/// </summary>
public sealed record Label(double X, double Y, string Text, double Size, string Anchor = "middle", bool Bold = false) : Shape;

/// <summary>
/// A group of shapes the HTML viewer can toggle.
/// </summary>
public sealed class Layer
{
    public Layer(string name) => Name = name;
    public string Name { get; }
    public List<Shape> Shapes { get; } = new();
    public bool VisibleByDefault { get; init; } = true;
    public void Add(Shape s) => Shapes.Add(s);
}

public sealed class Scene
{
    public string Title { get; set; } = "";
    public List<string> Notes { get; } = new();    // free-form lines shown beside the drawing
    public List<Layer> Layers { get; } = new();    // draw order, first = bottom

    /// <summary>
    /// Get a layer by name, creating it (on top) if needed.
    /// </summary>
    public Layer this[string name]
    {
        get
        {
            Layer? layer = Layers.FirstOrDefault(x => x.Name == name);

            if (layer is null)
            {
                layer = new Layer(name);
                Layers.Add(layer);
            }

            return layer;
        }
    }

    public Bounds Bounds()
    {
        Bounds b = Viz.Bounds.Empty;

        foreach (Layer layer in Layers)
        {
            foreach (Shape s in layer.Shapes)
            {
                b = b.Union(Viz.Bounds.Of(s));
            }
        }

        return b;
    }
}

public readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public static readonly Bounds Empty = new(double.PositiveInfinity, double.PositiveInfinity,
                                              double.NegativeInfinity, double.NegativeInfinity);
    public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public Bounds Union(Bounds o) => new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY),
                                         Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));

    public Bounds Pad(double fraction)
    {
        if (IsEmpty)
        {
            return new Bounds(-1, -1, 1, 1);
        }

        double p = Math.Max(Width, Height) * fraction;

        if (p <= 0)
        {
            p = 1;
        }

        return new Bounds(MinX - p, MinY - p, MaxX + p, MaxY + p);
    }

    public static Bounds Of(Shape s) => s switch
    {
        Circle c => new(c.X - c.R, c.Y - c.R, c.X + c.R, c.Y + c.R),
        Rect r => new(Math.Min(r.X, r.X + r.W), Math.Min(r.Y, r.Y + r.H), Math.Max(r.X, r.X + r.W), Math.Max(r.Y, r.Y + r.H)),
        Line l => new(Math.Min(l.X1, l.X2), Math.Min(l.Y1, l.Y2), Math.Max(l.X1, l.X2), Math.Max(l.Y1, l.Y2)),
        Polyline p when p.Points.Count > 0 => new(p.Points.Min(q => q.X), p.Points.Min(q => q.Y), p.Points.Max(q => q.X), p.Points.Max(q => q.Y)),
        Label t => LabelBounds(t),
        _ => Empty,
    };

    // Rough text extent (avg glyph ~0.55em) so to avoid clipping.
    private static Bounds LabelBounds(Label t)
    {
        double w = t.Text.Length * t.Size * 0.55;
        double x0 = t.Anchor switch { "start" => t.X, "end" => t.X - w, _ => t.X - w / 2 };

        return new(x0, t.Y - t.Size, x0 + w, t.Y + t.Size);
    }
}