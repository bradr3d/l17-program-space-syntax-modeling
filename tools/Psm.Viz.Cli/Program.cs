// psm-viz: run the bubble solver on a program JSON and export visuals.
//
// Usage:
//
//   dotnet run --project tools/Psm.Viz.Cli -- samples/program.golden.json
//   dotnet run --project tools/Psm.Viz.Cli -- samples/program.golden.json --out viz --every 5 --seed 7 --k-dept 0.03
//
// Writes <out>/bubbles.svg (final layout) and <out>/bubbles-replay.html (every Nth iteration).

using BubbleSolver;
using Psm.Viz;
using Psm.Viz.Cli;
using Solver = BubbleSolver.BubbleSolver;   // class has the same name as its namespace

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: psm-viz <program.json> [--out DIR] [--every N] [--seed N] [--k-dept K]");
    return 1;
}

string input = args[0];
string outDir = Opt("--out", "viz-out");
int every = Math.Max(1, int.Parse(Opt("--every", "10")));
BubbleSolverConfig defaults = new();
int seed = int.Parse(Opt("--seed", defaults.Seed.ToString()));
double kDepartmentCohesion = double.Parse(Opt("--k-dept", defaults.KDepartmentCohesion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                           System.Globalization.CultureInfo.InvariantCulture);

LoadedProgram program = LoadedProgram.Read(input);
BubbleSolverConfig config = new()
{
    UnitSystem = program.Units,
    Seed = seed,
    KDepartmentCohesion = kDepartmentCohesion
};

// Keep every Nth frame, plus the first/last of each phase
List<BubbleFrame> frames = new();
BubbleFrame? pending = null;
SolverPhase? lastPhase = null;
void OnFrame(BubbleFrame f)
{
    bool phaseChanged = f.Phase != lastPhase;
    if (phaseChanged && pending is not null && frames.LastOrDefault() != pending) frames.Add(pending);
    if (phaseChanged || f.Iteration % every == 0) frames.Add(f);
    pending = f;
    lastPhase = f.Phase;
}

BubbleSolverResult result = Solver.Solve(program.ToSolverRooms(), program.ToSolverEdges(), config, OnFrame);

// Internal solver units are meters, while the padding is given in input units.
double padModel = config.Padding * (config.UnitSystem == LinearUnit.Meters ? 1.0 : 0.3048);
string title = $"Bubble solver � {program.SourceName}";

Directory.CreateDirectory(outDir);

// 1) final layout as a static SVG
FloorLayout finalLayout = FloorLayout.From(new[] { (IReadOnlyList<BubbleResult>)result.Rooms });
Scene finalScene = BubbleScenes.Build(result.Rooms, program, finalLayout, padModel, title);
finalScene.Notes.InsertRange(0, result.Info.Split(Environment.NewLine));
string svgPath = Path.Combine(outDir, "bubbles.svg");
File.WriteAllText(svgPath, SvgWriter.Write(finalScene));

// 2) replay of the run as one HTML file
FloorLayout layout = FloorLayout.From(frames.Select(f => (IReadOnlyList<BubbleResult>)f.Rooms));
List<Frame> replay = frames.Select(f => new Frame(
        $"{f.Phase} {f.Iteration}",
        BubbleScenes.Build(f.Rooms, program, layout, padModel, title)))
    .ToList();
string htmlPath = Path.Combine(outDir, "bubbles-replay.html");
File.WriteAllText(htmlPath, HtmlReplayWriter.Write(title, replay, result.Info.Split(Environment.NewLine)));

Console.WriteLine(result.Info);
Console.WriteLine($"wrote {svgPath}");
Console.WriteLine($"wrote {htmlPath}  ({replay.Count} frames)");
return 0;

string Opt(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}