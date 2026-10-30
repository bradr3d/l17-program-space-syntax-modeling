// Scene -> SVG.
// This can open in any browser, and because it's an SVG, scales without any blurring.
// It can also be imported into programs like Illustrator/Inkscape/Rhino.

using System.Globalization;
using System.Net;
using System.Text;

namespace Psm.Viz;

public sealed class SvgOptions
{
    public int WidthPx { get; init; } = 1400;          // height follows the scene's aspect ratio
    public Bounds? View { get; init; }                 // null = fit the scene
    public double Margin { get; init; } = 0.04;        // fraction of the scene's larger side
    public bool Header { get; init; } = true;          // title + notes above the drawing
    public string Background { get; init; } = "#ffffff";
}

/// <summary>
/// Maps each distinct Style to a CSS class.
/// </summary>
public sealed class StyleSheet
{
    private readonly Dictionary<(Style, bool), string> _classes = new();
    private readonly StringBuilder _css = new();

    public string ClassFor(Style s, bool isText)
    {
        if (_classes.TryGetValue((s, isText), out string? existing))
        {
            return existing;
        }

        string name = "s" + _classes.Count.ToString(CultureInfo.InvariantCulture);
        _classes[(s, isText)] = name;

        _css.Append('.').Append(name).Append('{');

        if (isText)
        {
            _css.Append("fill:").Append(s.TextColor ?? s.Stroke).Append(";stroke:none;");
        }
        else
        {
            _css.Append("fill:").Append(s.Fill).Append(';');
            if (s.FillOpacity < 1)
            {
                _css.Append("fill-opacity:").Append(N(s.FillOpacity)).Append(';');
            }

            _css.Append("stroke:").Append(s.Stroke).Append(';');
            _css.Append("stroke-width:").Append(N(s.StrokeWidth)).Append(';');

            if (s.StrokeOpacity < 1)
            {
                _css.Append("stroke-opacity:").Append(N(s.StrokeOpacity)).Append(';');
            }

            if (s.Dash is not null)
            {
                _css.Append("stroke-dasharray:").Append(s.Dash).Append(';');
            }
        }

        _css.Append("}\n");

        return name;
    }

    public string Css =>
        "svg.psm *{vector-effect:non-scaling-stroke}\n" +
        "svg.psm text{font-family:system-ui,-apple-system,'Segoe UI',sans-serif;dominant-baseline:central}\n" +
        _css;

    internal static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

public static class SvgWriter
{
    /// <summary>
    /// A standalone .svg document.
    /// </summary>
    public static string Write(Scene scene, SvgOptions? options = null)
    {
        SvgOptions o = options ?? new SvgOptions();
        Bounds view = o.View ?? scene.Bounds().Pad(o.Margin);
        StyleSheet sheet = new();
        string body = WriteBody(scene, sheet);

        int w = o.WidthPx;
        int h = (int)Math.Ceiling(w * view.Height / Math.Max(view.Width, 1e-9));
        List<string> headerLines = o.Header ? scene.Notes : new List<string>();
        int headerH = o.Header ? 34 + 17 * headerLines.Count : 0;

        StringBuilder sb = new();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"psm\" width=\"{w}\" height=\"{h + headerH}\" viewBox=\"0 0 {w} {h + headerH}\">\n");
        sb.Append("<style>\n").Append(sheet.Css)
          .Append("svg.psm .hdr{font:600 16px system-ui,sans-serif;fill:#111}\n")
          .Append("svg.psm .note{font:12px ui-monospace,Consolas,monospace;fill:#444;dominant-baseline:auto}\n")
          .Append("</style>\n");
        sb.Append(Defs);
        sb.Append($"<rect width=\"100%\" height=\"100%\" fill=\"{o.Background}\"/>\n");

        if (o.Header)
        {
            sb.Append($"<text class=\"hdr\" x=\"16\" y=\"22\">{Esc(scene.Title)}</text>\n");
            for (int i = 0; i < headerLines.Count; i++)
                sb.Append($"<text class=\"note\" x=\"16\" y=\"{44 + 17 * i}\">{Esc(headerLines[i])}</text>\n");
        }

        sb.Append($"<svg x=\"0\" y=\"{headerH}\" width=\"{w}\" height=\"{h}\" viewBox=\"{ViewBox(view)}\" preserveAspectRatio=\"xMidYMid meet\">\n");
        sb.Append(body);
        sb.Append("</svg>\n</svg>\n");

        return sb.ToString();
    }

    /// <summary>
    /// "minX minY w h" in SVG space (Y flipped).
    /// </summary>
    public static string ViewBox(Bounds b) => $"{N(b.MinX)} {N(-b.MaxY)} {N(b.Width)} {N(b.Height)}";

    public const string Defs =
        "<defs><marker id=\"psm-arrow\" viewBox=\"0 0 10 10\" refX=\"9\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" " +
        "orient=\"auto-start-reverse\" markerUnits=\"userSpaceOnUse\"><path d=\"M0,0 L10,5 L0,10 z\" fill=\"#555\"/></marker></defs>\n";

    /// <summary>
    /// Just the layer groups (world coordinates, Y flipped). Used by the HTML replay.
    /// </summary>
    public static string WriteBody(Scene scene, StyleSheet sheet)
    {
        StringBuilder sb = new();

        foreach (Layer layer in scene.Layers)
        {
            sb.Append($"<g data-layer=\"{Esc(Slug(layer.Name))}\">\n");

            foreach (Shape s in layer.Shapes)
            {
                AppendShape(sb, s, sheet);
            }

            sb.Append("</g>\n");
        }

        return sb.ToString();
    }

    public static string Slug(string name)
    {
        StringBuilder sb = new();

        foreach (char ch in name.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        return sb.ToString();
    }

    private static void AppendShape(StringBuilder sb, Shape s, StyleSheet sheet)
    {
        string cls = sheet.ClassFor(s.Style, s is Label);
        string open;
        string close;

        switch (s)
        {
            case Circle c:
                open = $"<circle class=\"{cls}\" cx=\"{N(c.X)}\" cy=\"{N(-c.Y)}\" r=\"{N(c.R)}\"";
                close = "circle";
                break;

            case Rect r:
                double x = Math.Min(r.X, r.X + r.W);
                double y = Math.Max(r.Y, r.Y + r.H);
                open = $"<rect class=\"{cls}\" x=\"{N(x)}\" y=\"{N(-y)}\" width=\"{N(Math.Abs(r.W))}\" height=\"{N(Math.Abs(r.H))}\"";
                close = "rect";
                break;

            case Line l:
                open = $"<line class=\"{cls}\" x1=\"{N(l.X1)}\" y1=\"{N(-l.Y1)}\" x2=\"{N(l.X2)}\" y2=\"{N(-l.Y2)}\"" +
                       (l.Arrow ? " marker-end=\"url(#psm-arrow)\"" : "");
                close = "line";
                break;

            case Polyline p:
                string pts = string.Join(" ", p.Points.Select(q => $"{N(q.X)},{N(-q.Y)}"));
                close = p.Closed ? "polygon" : "polyline";
                open = $"<{close} class=\"{cls}\" points=\"{pts}\"";
                break;

            case Label t:
                sb.Append($"<text class=\"{cls}\" x=\"{N(t.X)}\" y=\"{N(-t.Y)}\" font-size=\"{N(t.Size)}\" text-anchor=\"{t.Anchor}\"")
                  .Append(t.Bold ? " font-weight=\"600\"" : "")
                  .Append('>').Append(Esc(t.Text));

                if (s.Tooltip is not null)
                {
                    sb.Append("<title>").Append(Esc(s.Tooltip)).Append("</title>");
                }

                sb.Append("</text>\n");

                return;

            default:
                return;
        }

        if (s.Tooltip is null)
        {
            sb.Append(open).Append("/>\n");
        }
        else
        {
            sb.Append(open).Append("><title>").Append(Esc(s.Tooltip)).Append("</title></").Append(close).Append(">\n");
        }
    }

    private static string N(double v) => StyleSheet.N(v);
    internal static string Esc(string s) => WebUtility.HtmlEncode(s);
}