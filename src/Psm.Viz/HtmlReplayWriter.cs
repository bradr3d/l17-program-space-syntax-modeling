// A list of Scenes (e.g. solver iterations) -> .html file with
// play/pause, a scrub slider, layer toggles, hover tooltips, zoom/pan, and a
// "save this frame as SVG" button.
//
// The page's CSS and JS live in Viewer/viewer.css and Viewer/viewer.js.
// They are embedded resources (see Psm.Viz.csproj), so edit them as normal files.

using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Psm.Viz;

public sealed record Frame(string Caption, Scene Scene);

public static class HtmlReplayWriter
{
    private static readonly string ViewerCss = LoadViewerFile("viewer.css");
    private static readonly string ViewerJs = LoadViewerFile("viewer.js");

    public static string Write(string title, IReadOnlyList<Frame> frames, IEnumerable<string>? notes = null)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("Need at least one frame.", nameof(frames));
        }

        // One fixed view for every frame.
        Bounds view = Bounds.Empty;

        foreach (Frame f in frames)
        {
            view = view.Union(f.Scene.Bounds());
        }

        view = view.Pad(0.04);

        StyleSheet sheet = new();
        List<string> bodies = frames.Select(f => SvgWriter.WriteBody(f.Scene, sheet)).ToList();
        List<string> captions = frames.Select(f => f.Caption).ToList();

        List<(string Slug, string Name, bool On)> layers = new();

        foreach (Frame f in frames)
        {
            foreach (Layer l in f.Scene.Layers)
            {
                if (!layers.Any(x => x.Name == l.Name))
                {
                    layers.Add((SvgWriter.Slug(l.Name), l.Name, l.VisibleByDefault));
                }
            }
        }

        List<string> noteLines = (notes ?? frames[^1].Scene.Notes).ToList();

        List<string> layerToggles = new();

        foreach ((string slug, string name, bool on) in layers)
        {
            string checkedAttr = on ? " checked" : "";
            layerToggles.Add(
                $"<label class=\"layer-toggle\"><input type=\"checkbox\" data-layer=\"{SvgWriter.Esc(slug)}\"{checkedAttr}> {SvgWriter.Esc(name)}</label>");
        }

        string safeTitle = SvgWriter.Esc(title);

        // $$ raw string: {{...}} are C# holes, single braces are literal text.
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{safeTitle}}</title>
                <style>
            {{Indent(ViewerCss, 8)}}
                </style>
                <style>
                    /* shape styles generated for this run (StyleSheet) */
            {{Indent(sheet.Css, 8)}}
                </style>
            </head>
            <body>
                <header>
                    <h1>{{safeTitle}}</h1>
                    <span id="caption"></span>
                </header>

                <main>
                    <div id="stage-wrap">
                        <svg id="stage" class="psm" xmlns="http://www.w3.org/2000/svg"
                             viewBox="{{SvgWriter.ViewBox(view)}}" preserveAspectRatio="xMidYMid meet">
                            {{SvgWriter.Defs.Trim()}}
                            <g id="frame"></g>
                        </svg>
                    </div>

                    <aside>
                        <div class="controls">
                            <button id="play">▶ Play</button>
                            <label>speed
                                <select id="fps">
                                    <option>5</option>
                                    <option selected>15</option>
                                    <option>30</option>
                                    <option>60</option>
                                </select> fps
                            </label>
                        </div>

                        <input id="slider" type="range" min="0" max="{{frames.Count - 1}}" value="{{frames.Count - 1}}">

                        <div class="controls">
                            <button id="prev">◀</button>
                            <button id="next">▶</button>
                            <button id="fit">Reset view</button>
                            <button id="save">Save frame .svg</button>
                        </div>

                        <h2>Layers</h2>
            {{Indent(string.Join("\n", layerToggles), 12)}}

                        <h2>Run</h2>
                        <pre>{{SvgWriter.Esc(string.Join("\n", noteLines))}}</pre>

                        <p class="hint">Scroll to zoom, drag to pan, hover a shape for details. ←/→ step, space plays.</p>
                    </aside>
                </main>

                <!-- Run data. One frame per line so view-source stays navigable. -->
                <script>
                    const REPLAY = {
                        view: {{Js(new[] { view.MinX, -view.MaxY, view.Width, view.Height })}},
                        css: {{Js(sheet.Css)}},
                        captions: {{Js(captions)}},
                        frames: [
            {{Indent(string.Join(",\n", bodies.Select(Js)), 16)}}
                        ],
                    };
                </script>

                <script>
            {{Indent(ViewerJs, 8)}}
                    //# sourceURL=psm-viewer.js
                </script>
            </body>
            </html>

            """;
    }

    // Relaxed escaping keeps quotes as \" instead of " (roughly halves file size).
    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Js<T>(T value) => JsonSerializer.Serialize(value, JsonOpts).Replace("</", "<\\/");

    /// <summary>
    /// Indents every non-empty line, so embedded files line up in view-source.
    /// </summary>
    private static string Indent(string text, int spaces)
    {
        string pad = new(' ', spaces);
        IEnumerable<string> lines = text.Replace("\r\n", "\n").TrimEnd().Split('\n')
                                        .Select(line => line.Length == 0 ? line : pad + line);

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Reads Viewer/viewer.css or Viewer/viewer.js out of this DLL.
    /// </summary>
    private static string LoadViewerFile(string fileName)
    {
        string resourceName = "Psm.Viz.Viewer." + fileName;
        Assembly assembly = typeof(HtmlReplayWriter).Assembly;

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. Check the <EmbeddedResource> items in Psm.Viz.csproj.");
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }
}
