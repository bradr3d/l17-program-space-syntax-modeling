using System.Globalization;
using System.Reflection;
using Psm.Data.Csv;
using Psm.Data.Json;
using Psm.Data.Model;
using Psm.Data.Serialization;

namespace Psm.Data.Tests;

/// <summary>CSV and JSON sources, the committed sample fixture, determinism, culture and the Rhino boundary.</summary>
public class SourceFormatTests
{
    private static ImportResult Sample() => new CsvProgramSource(Fixtures.PathOf("program.sample.csv")).Load();

    [Fact]
    public void Sample_csv_imports_cleanly()
    {
        var r = Sample();
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics.Select(d => d.Message)));
        var p = r.Program!;
        Assert.Equal(8, p.RoomTypes.Count);
        Assert.Equal(20, p.Instances.Count);
        Assert.Equal(20, p.Edges.Count);
        Assert.Equal(new Department("1", "Core Curricula - Classrooms"), p.Departments[0]);
        Assert.Equal(2, p.Summary!.DepartmentHeaderRows);
        Assert.Equal(new RgbColor(111, 115, 79), p.FindType("2.1")!.Color);
        Assert.Contains(new Edge("2.1.3#0", "2.1#0", EdgeKind.SharedWall), p.Edges);
        // CSV has no fills, so taking color from Color RGB is normal there (no W007/W008).
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity == Severity.Warning);
    }

    /// <summary>program.sample.json is the canonical JSON teammates can load without Excel.</summary>
    [Fact]
    public void Sample_json_fixture_matches_csv_import()
    {
        Fixtures.AssertMatchesGolden("program.sample.json", PsmJson.Serialize(Sample().Program));
    }

    [Fact]
    public void Json_round_trip_is_byte_identical()
    {
        var json = PsmJson.Serialize(Sample().Program);
        var back = JsonProgramSource.Parse(json);
        Assert.True(back.Ok);
        Assert.Equal(json, PsmJson.Serialize(back.Program));
    }

    [Fact]
    public void Json_with_edited_content_gets_a_hash_warning()
    {
        var json = PsmJson.Serialize(Sample().Program).Replace("\"Kiln\"", "\"Kiln Room\"");
        var r = JsonProgramSource.Parse(json);
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("programHash"));
    }

    [Fact]
    public void Json_from_a_newer_schema_is_rejected_without_throwing()
    {
        var json = PsmJson.Serialize(Sample().Program).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        var r = JsonProgramSource.Parse(json);
        Assert.Null(r.Program);
        Assert.Contains("99", r.Diagnostics.Single().Message);
    }

    [Fact]
    public void Hash_ignores_source_diagnostics_and_summary()
    {
        var p = Sample().Program!;
        var other = p with { Source = null, Diagnostics = [], Summary = null };
        Assert.Equal(p.ProgramHash, ProgramHasher.Compute(other));
        Assert.NotEqual(p.ProgramHash, ProgramHasher.Compute(p with { RoomTypes = p.RoomTypes.Skip(1).ToList() }));
    }

    [Fact]
    public void Csv_handles_quotes_commas_and_line_breaks_in_fields()
    {
        const string csv = "Dept No,Program Element,Room Qty/ Units,Net SF/ Units,Conn. Primary,Notes/Comments\r\n" +
                           "1.1,\"Office, \"\"Large\"\"\",1,100,,\"line one\nline two\"\r\n";
        var r = CsvProgramSource.Parse(csv);
        var t = r.Program!.FindType("1.1")!;
        Assert.Equal("Office, \"Large\"", t.Name);
        Assert.Equal("line one\nline two", t.Notes);
    }

    [Fact]
    public void Csv_keeps_7_10_as_text()
    {
        const string csv = "Dept No,Program Element,Room Qty/ Units,Net SF/ Units,Conn. Primary\n7.1,A,1,10,\n7.10,B,1,10,7.1\n";
        var p = CsvProgramSource.Parse(csv).Program!;
        Assert.Equal(["7.1", "7.10"], p.RoomTypes.Select(t => t.Id));
    }

    [Fact]
    public void Output_does_not_depend_on_current_culture()
    {
        var invariant = PsmJson.Serialize(Sample().Program);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(invariant, PsmJson.Serialize(Sample().Program));
            var fromXlsx = new WorkbookBuilder().Room("1.1", "Room", 1, 12.5).Import();
            Assert.Equal(12.5, fromXlsx.Program!.FindType("1.1")!.NetSf);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = saved;
        }
    }

    [Fact]
    public void Psm_Data_never_references_Rhino_Grasshopper_or_Eto()
    {
        string[] banned = ["Rhino", "Grasshopper", "Eto"];
        var seen = new HashSet<string>();
        var queue = new Queue<AssemblyName>([typeof(BuildingProgram).Assembly.GetName()]);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name.Name!)) continue;
            Assert.False(banned.Any(b => name.Name!.StartsWith(b, StringComparison.OrdinalIgnoreCase)),
                $"Psm.Data depends on {name.Name}, which is not allowed (it must build and test on Linux).");
            Assembly asm;
            try { asm = Assembly.Load(name); }
            catch (FileNotFoundException) { continue; }
            foreach (var r in asm.GetReferencedAssemblies()) queue.Enqueue(r);
        }
        Assert.Contains("ClosedXML", seen);
    }
}
