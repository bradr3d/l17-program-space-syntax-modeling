// Psm.Inspect: import a program file and print what the DAL made from it.
//
//   dotnet run --project tools/Psm.Inspect -- <file.xlsx|.xlsm|.csv|.json> [section] [--json out.json]
//
// Sections: summary (default), departments, types, rooms, edges, chains, diagnostics, all

using System.Globalization;
using Psm.Data;
using Psm.Data.Csv;
using Psm.Data.Excel;
using Psm.Data.Json;
using Psm.Data.Model;
using Psm.Data.Serialization;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var positional = args.Where(a => !a.StartsWith("--")).ToList();
var jsonOut = args.SkipWhile(a => a != "--json").Skip(1).FirstOrDefault();
if (positional.Count == 0)
{
    Console.WriteLine("Usage: dotnet run --project tools/Psm.Inspect -- <file> [summary|departments|types|rooms|edges|chains|diagnostics|all] [--json out.json]");
    return 1;
}

var path = positional[0];
var section = positional.Count > 1 ? positional[1].ToLowerInvariant() : "summary";

IProgramSource source = Path.GetExtension(path).ToLowerInvariant() switch
{
    ".xlsx" or ".xlsm" => new ExcelProgramSource(path),
    ".csv" => new CsvProgramSource(path),
    ".json" => new JsonProgramSource(path),
    var ext => throw new ArgumentException($"Don't know how to read '{ext}' files."),
};

var result = source.Load();
var p = result.Program;
bool Show(string name) => section == name || section == "all";

if (p is null)
{
    Header("Import failed");
    PrintDiagnostics(result.Diagnostics);
    return 2;
}

if (Show("summary"))
{
    var s = p.Summary!;
    Header("Summary");
    Console.WriteLine($"File            {p.Source?.FileName}  (sheet: {p.Source?.Sheet ?? "-"})");
    if (p.Source?.Project is { } proj)
        Console.WriteLine($"Project         {proj.Name} | client {proj.Client} | date {proj.Date} | rev {proj.Revision} | no. {proj.Number}");
    Console.WriteLine($"Status          {(result.Ok ? "OK" : "HAS ERRORS")}  ({Count(Severity.Error)} errors, {Count(Severity.Warning)} warnings, {Count(Severity.Info)} info)");
    Console.WriteLine($"Rows read       {s.RowsRead}  → {s.RoomTypes} room types, {s.DividerRowsSkipped} dividers, {s.BlankRowsSkipped} blank, {s.DepartmentHeaderRows} dept headers, {s.InvalidRowsSkipped} invalid");
    Console.WriteLine($"Room types      {s.RoomTypes}  ({s.AbsorbedRoomTypes} absorbed, Qty 0)");
    Console.WriteLine($"Rooms           {s.Instances}");
    Console.WriteLine($"Edges           {s.Edges}  ({s.SharedWallEdges} shared-wall, {s.Edges - s.SharedWallEdges} adjacent)");
    Console.WriteLine($"Unresolved      {s.UnresolvedRoomTypes} room type(s), {s.EdgesSkipped} edge(s) skipped");
    Console.WriteLine($"Total net SF    {s.TotalNetSf:#,##0}");
    foreach (var f in s.FloorPreferences)
        Console.WriteLine($"  {(f.Floor is { } n ? $"floor {n}" : "no pref"),-10}  {f.Rooms,4} rooms  {f.NetSf,10:#,##0} SF");
    Console.WriteLine($"Shared chains   {SharedChains.Compute(p).Count}  ({SharedChains.Compute(p).Count(c => c.Count > 1)} with more than one room)");
    Console.WriteLine($"Program hash    {p.ProgramHash}");
    if (section == "summary")
        Console.WriteLine("\nMore detail: add a section name after the file — departments, types, rooms, edges, chains, diagnostics, all");
}

if (Show("departments"))
{
    Header("Departments");
    foreach (var d in p.Departments)
    {
        var types = p.RoomTypes.Where(t => t.DepartmentId == d.Id).ToList();
        var rooms = p.Instances.Count(i => types.Any(t => t.Id == i.TypeId));
        Console.WriteLine($"{d.Id,3}  {d.Name ?? "(no name)",-42} {types.Count,3} types {rooms,4} rooms");
    }
}

if (Show("types"))
{
    Header("Room types (one per spreadsheet row)");
    Console.WriteLine($"{"Id",-8} {"Name",-30} {"Qty",4} {"Net SF",7} {"Conn",-7} {"Flr",3} {"Shared",6} {"Color",-13} {"Row",4}  Flags");
    foreach (var t in p.RoomTypes)
    {
        var flags = string.Join(" ", new[]
        {
            t.Absorbed ? "ABSORBED" : null,
            t.MinWidthFt is { } w ? $"minW={w}" : null,
            t.Aspect is { } a ? $"aspect={a}" : null,
        }.Where(x => x is not null));
        Console.WriteLine($"{t.Id,-8} {Trunc(t.Name, 30),-30} {t.Qty,4} {t.NetSf,7:#,##0} {t.ConnPrimary ?? "-",-7} {t.FloorPref?.ToString() ?? "-",3} {(t.SharedAccess ? "x" : ""),6} {t.Color,-13} {t.Source?.Row,4}  {flags}");
    }
}

if (Show("rooms"))
{
    Header("Rooms (instances)");
    foreach (var i in p.Instances)
    {
        var t = p.FindType(i.TypeId)!;
        Console.WriteLine($"{i.Key,-10} {i.Label,-45} {t.NetSf,6:#,##0} SF  dept {t.DepartmentId}  flr {t.FloorPref?.ToString() ?? "-"}");
    }
}

if (Show("edges"))
{
    Header("Edges (from → to)");
    foreach (var e in p.Edges)
        Console.WriteLine($"{e.FromKey,-10} → {e.ToKey,-10} {(e.Kind == EdgeKind.SharedWall ? "shared wall" : "adjacent")}");
}

if (Show("chains"))
{
    Header("Shared-access chains with more than one room (must stay on one floor)");
    foreach (var c in SharedChains.Compute(p).Where(c => c.Count > 1))
        Console.WriteLine(string.Join(" + ", c));
}

if (Show("diagnostics"))
{
    Header("Diagnostics");
    PrintDiagnostics(result.Diagnostics);
}

if (jsonOut is not null)
{
    File.WriteAllText(jsonOut, PsmJson.Serialize(p));
    Console.WriteLine($"\nWrote canonical JSON to {Path.GetFullPath(jsonOut)}");
}

return result.Ok ? 0 : 2;

int Count(Severity s) => result.Diagnostics.Count(d => d.Severity == s);

static void Header(string title)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine(new string('─', title.Length));
}

static void PrintDiagnostics(IReadOnlyList<Diagnostic> diags)
{
    if (diags.Count == 0) Console.WriteLine("(none)");
    foreach (var d in diags)
    {
        var where = d.Row is { } r ? $"{d.Sheet}!{d.Column}{r}" : d.Sheet ?? "";
        Console.WriteLine($"{d.Code} {d.Severity.ToString().ToUpperInvariant(),-7} {where,-16} {d.Message}");
    }
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
