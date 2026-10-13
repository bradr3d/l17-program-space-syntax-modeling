using ClosedXML.Excel;
using Psm.Data.Excel;
using Psm.Data.Model;

namespace Psm.Data.Tests;

/// <summary>
/// The real test workbook, committed at Fixtures/Program_ExcelToGrasshopper_Test.xlsm.
/// Set PSM_TEST_WORKBOOK to run the workbook tests against a different copy.
/// </summary>
public static class TestWorkbook
{
    public const string EnvVar = "PSM_TEST_WORKBOOK";
    public const string FileName = "Program_ExcelToGrasshopper_Test.xlsm";

    public static string Path
    {
        get
        {
            var p = Environment.GetEnvironmentVariable(EnvVar);
            return string.IsNullOrWhiteSpace(p) ? Fixtures.PathOf(FileName) : p;
        }
    }

    private static readonly Lazy<ImportResult> Cached = new(() => new ExcelProgramSource(Path).Load());

    /// <summary>One import shared by read-only assertions (the import is deterministic).</summary>
    public static ImportResult Import() => Cached.Value;
}

public static class Fixtures
{
    /// <summary>tests/Psm.Data.Tests/Fixtures in the source tree (not the bin copy), so golden updates land in git.</summary>
    public static string Dir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Psm.Data.Tests.csproj")))
                dir = dir.Parent;
            return System.IO.Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Psm.Data.Tests.csproj not found"), "Fixtures");
        }
    }

    public static string PathOf(string name) => System.IO.Path.Combine(Dir, name);

    public static bool UpdateGolden => Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1";

    /// <summary>Compares text to a golden file. With UPDATE_GOLDEN=1, rewrites the file instead.</summary>
    public static void AssertMatchesGolden(string name, string actual)
    {
        var path = PathOf(name);
        if (UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"Golden file {name} is missing. Run `UPDATE_GOLDEN=1 dotnet test`, then review it in git.");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (expected == actual) return;

        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var line = 0;
        while (line < Math.Min(e.Length, a.Length) && e[line] == a[line]) line++;
        Assert.Fail($"{name} differs from the import at line {line + 1}.\n  expected: {(line < e.Length ? e[line] : "<end>")}\n  actual:   {(line < a.Length ? a[line] : "<end>")}\n" +
                    "If the change is intended, run `UPDATE_GOLDEN=1 dotnet test` and review the diff in git.");
    }
}

/// <summary>
/// Builds small .xlsx program workbooks in code, so each broken case is visible in the test itself.
/// </summary>
public sealed class WorkbookBuilder
{
    public static readonly string[] StandardHeaders =
    [
        "Dept No", "Program Element", "Room Qty/ Units", "Net SF/ Units", "Total Program SF ",
        "Design Capacity / Student Stations", "Total Stations ", "Notes/Comments", "Conn. Primary", "Flr",
        "Color", "Color RGB", "Shared Access", "Room Width Min.", "Aspect Ratio",
    ];

    private readonly XLWorkbook _wb = new();
    private readonly IXLWorksheet _ws;
    private int _next = 2;

    public WorkbookBuilder(string sheet = "ProgramInfo", string[]? headers = null)
    {
        _ws = _wb.AddWorksheet(sheet);
        var h = headers ?? StandardHeaders;
        for (var i = 0; i < h.Length; i++) _ws.Cell(1, i + 1).Value = h[i];
    }

    public IXLWorksheet Sheet => _ws;

    /// <summary>Adds a row. Values go into columns A.. in order; null leaves a cell blank.</summary>
    public WorkbookBuilder Row(params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var cell = _ws.Cell(_next, i + 1);
            cell.Value = values[i] switch
            {
                null => Blank.Value,
                string s => s,
                int n => n,
                double d => d,
                bool b => b,
                _ => throw new ArgumentException($"Unsupported value {values[i]}"),
            };
        }
        _next++;
        return this;
    }

    /// <summary>A standard room row: id, name, qty, net, conn, shared, fill.</summary>
    public WorkbookBuilder Room(object id, string name, object qty, object net, object? conn = null, bool shared = false,
        XLColor? fill = null, object? total = null, object? floor = null, object? minWidth = null, string? rgb = "255, 255, 255", bool noFill = false)
    {
        var q = qty;
        var n = net;
        var t = total ?? (q is int qi && n is int ni ? qi * ni : null);
        Row(id, name, q, n, t, 0, 0, 0, conn ?? 0, floor, null, rgb, shared ? "x" : null, minWidth, null);
        if (!noFill) _ws.Cell(_next - 1, 11).Style.Fill.SetBackgroundColor(fill ?? XLColor.FromArgb(168, 174, 73));
        return this;
    }

    public IXLCell Cell(int row, int col) => _ws.Cell(row, col);

    public int LastRow => _next - 1;

    public string Save(string? dir = null)
    {
        dir ??= Directory.CreateTempSubdirectory("psm-test-").FullName;
        var path = System.IO.Path.Combine(dir, "program.xlsx");
        _wb.SaveAs(path);
        _wb.Dispose();
        return path;
    }

    public ImportResult Import(ExcelImportOptions? options = null) => new ExcelProgramSource(Save(), options).Load();
}

public static class DiagnosticAsserts
{
    public static IReadOnlyList<Diagnostic> WithCode(this ImportResult r, string code) =>
        r.Diagnostics.Where(d => d.Code == code).ToList();
}
