using System.Security.Cryptography;
using Psm.Data.Excel;
using Psm.Data.Model;
using Psm.Data.Serialization;

namespace Psm.Data.Tests;

/// <summary>Imports the real test workbook (Fixtures, or PSM_TEST_WORKBOOK) and checks the numbers in CLAUDE.md.</summary>
public class GoldenWorkbookTests
{
    private static BuildingProgram Program => TestWorkbook.Import().Program!;

    [Fact]
    public void Golden_numbers_match_the_validated_prototype_dataset()
    {
        var result = TestWorkbook.Import();
        Assert.NotNull(result.Program);
        var p = result.Program!;
        var s = p.Summary!;

        Assert.Equal(72, p.RoomTypes.Count);
        Assert.Equal(4, p.RoomTypes.Count(t => t.Absorbed));
        Assert.Equal(151, p.Instances.Count);
        Assert.Equal(145, p.Edges.Count);
        Assert.Equal(1, s.UnresolvedRoomTypes);
        Assert.Equal(6, s.EdgesSkipped);
        Assert.Equal(48_092, s.TotalNetSf);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "9"], p.Departments.Select(d => d.Id));
        Assert.Empty(result.WithCode(DiagnosticCodes.DuplicateId));
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message)));
    }

    [Fact]
    public void Serialized_json_matches_golden_file()
    {
        Fixtures.AssertMatchesGolden("program.golden.json", PsmJson.Serialize(Program));
    }

    [Fact]
    public void Summary_counts_every_skipped_row()
    {
        var s = Program.Summary!;
        Assert.Equal(81, s.RowsRead);
        Assert.Equal(9, s.DividerRowsSkipped);
        Assert.Equal(0, s.BlankRowsSkipped);
        Assert.Equal(0, s.InvalidRowsSkipped);
        Assert.Equal(s.RowsRead, s.RoomTypes + s.DividerRowsSkipped + s.BlankRowsSkipped + s.InvalidRowsSkipped + s.DepartmentHeaderRows);
    }

    [Fact]
    public void Row_61_reads_as_7_10_from_its_number_format()
    {
        var t = Program.FindType("7.10");
        Assert.NotNull(t);
        Assert.Equal("CAE Workroom/Shared Resource", t!.Name);
        Assert.Equal(61, t.Source!.Row);
        Assert.Equal("General Admin Reception", Program.FindType("7.1")!.Name);
        // Row 62's Conn. Primary is also the number 7.1 formatted "0.00".
        Assert.Equal("7.10", Program.FindType("7.10.1")!.ConnPrimary);

        var w004 = TestWorkbook.Import().WithCode(DiagnosticCodes.IdStoredAsNumber);
        Assert.Equal([(61, "A"), (62, "I")], w004.Select(d => (d.Row!.Value, d.Column!)));
        Assert.All(w004, d => Assert.Equal(Severity.Info, d.Severity));
    }

    [Fact]
    public void Color_comes_from_the_K_fill_not_the_stale_L_value()
    {
        Assert.Equal(new RgbColor(168, 174, 73), Program.FindType("1.1")!.Color);
        Assert.DoesNotContain(Program.RoomTypes, t => t.Color == RgbColor.White);
        var w008 = Assert.Single(TestWorkbook.Import().WithCode(DiagnosticCodes.RgbDisagreesWithFill));
        Assert.Null(w008.Row);
        Assert.Empty(TestWorkbook.Import().WithCode(DiagnosticCodes.NoFillColor));
    }

    [Fact]
    public void Divider_rows_are_skipped()
    {
        int[] dividers = [17, 22, 31, 37, 41, 46, 70, 76, 82];
        Assert.DoesNotContain(Program.RoomTypes, t => dividers.Contains(t.Source!.Row));
    }

    [Fact]
    public void Qty_zero_rows_are_kept_as_absorbed_types_with_no_instances()
    {
        string[] absorbed = ["3.2", "3.4", "4.4", "7.11"];
        Assert.Equal(absorbed, Program.RoomTypes.Where(t => t.Absorbed).Select(t => t.Id));
        Assert.DoesNotContain(Program.Instances, i => absorbed.Contains(i.TypeId));
        Assert.Equal(absorbed, TestWorkbook.Import().WithCode(DiagnosticCodes.Absorbed).Select(d => d.TypeId));
    }

    [Fact]
    public void Vestibule_to_restroom_is_a_shared_wall_edge()
    {
        Assert.Contains(new Edge("1.1.2#0", "1.1.1#0", EdgeKind.SharedWall), Program.Edges);
        Assert.Contains(new Edge("1.1.1#0", "1.1#0", EdgeKind.SharedWall), Program.Edges);
        Assert.Contains(new Edge("1.1#0", "1.2#0", EdgeKind.Adjacent), Program.Edges);
        // 8.5 is top-level but has Shared Access onto its Conn. Primary 8.1, not an ID parent.
        Assert.Contains(new Edge("8.5#1", "8.1#1", EdgeKind.SharedWall), Program.Edges);
    }

    [Fact]
    public void Pairwise_wiring_clamps_to_the_targets_last_instance()
    {
        Assert.Contains(new Edge("7.10.1#2", "7.10#0", EdgeKind.Adjacent), Program.Edges);
        Assert.Contains(new Edge("6.1.2#3", "6.1#0", EdgeKind.Adjacent), Program.Edges);
    }

    [Fact]
    public void Labels_and_keys_match_the_prototype()
    {
        var i = Program.Instances.First(x => x.Key == "1.1#0");
        Assert.Equal("1.1 Classroom - K (1/4)", i.Label);
        Assert.Equal("7.11.1 Tutoring (1/6)", Program.Instances.First(x => x.TypeId == "7.11.1").Label);
    }

    [Fact]
    public void Known_anomalies_are_reported()
    {
        var r = TestWorkbook.Import();
        Assert.Equal(["7.1"], r.WithCode(DiagnosticCodes.TotalMismatch).Select(d => d.TypeId));
        Assert.Equal(["6.1.2", "7.10.1", "7.11.1"], r.WithCode(DiagnosticCodes.ChildQtyExceedsParent).Select(d => d.TypeId));
        Assert.Equal(["7.11.1"], r.WithCode(DiagnosticCodes.ConnectionToAbsorbed).Select(d => d.TypeId));
        var cycle = Assert.Single(r.WithCode(DiagnosticCodes.AdjacencyCycle));
        Assert.Contains("1.1 → 1.2 → 1.3 → 1.4 → 1.5 → 1.1", cycle.Message);
    }

    [Fact]
    public void Floor_preferences_and_flags_are_read()
    {
        Assert.Equal(1, Program.FindType("1.1")!.FloorPref);
        Assert.Null(Program.FindType("1.4")!.FloorPref);
        Assert.True(Program.FindType("1.1.1")!.SharedAccess);
        Assert.False(Program.FindType("1.1")!.SharedAccess);
        Assert.Equal(5, Program.FindType("1.1.1")!.MinWidthFt);
        Assert.Equal("1 to 1", Program.FindType("1.1")!.Aspect);
        Assert.Null(Program.FindType("1.1.1")!.Aspect); // "None"
        Assert.Null(Program.FindType("2.1.1")!.Notes);  // 0 = empty
        Assert.Equal("Tutoring", Program.FindType("7.11.1")!.Name); // trailing space trimmed
    }

    [Fact]
    public void Department_names_come_from_ProjectSheet_and_dept_8_uses_the_header_with_rooms()
    {
        Assert.Equal("Core Curricula - Classrooms", Program.Departments.Single(d => d.Id == "1").Name);
        Assert.Equal("Custodial/Building Support", Program.Departments.Single(d => d.Id == "8").Name);
        Assert.Equal("Athletics", Program.Departments.Single(d => d.Id == "6").Name);
        var i003 = Assert.Single(TestWorkbook.Import().WithCode(DiagnosticCodes.RepeatedDepartmentHeader));
        Assert.Equal(122, i003.Row);
    }

    [Fact]
    public void Project_metadata_is_read()
    {
        var p = Program.Source!.Project!;
        Assert.Equal("Lower School Concept Design", p.Name);
        Assert.Equal("2026-04-01", p.Date);
        Assert.Equal("2", p.Revision);
        Assert.Equal("27520", p.Number);
    }

    [Fact]
    public void Shared_access_chains_match_the_prototype()
    {
        // Handoff §2.5: "The program forms 93 atomic chains, 31 of them multi-room."
        var chains = SharedChains.Compute(Program);
        Assert.Equal(93, chains.Count);
        Assert.Equal(31, chains.Count(c => c.Count > 1));
        Assert.Contains(chains, c => c.SequenceEqual(["1.1#0", "1.1.1#0", "1.1.2#0"]));
    }

    [Fact]
    public void Importing_twice_gives_identical_bytes()
    {
        var a = PsmJson.Serialize(new ExcelProgramSource(TestWorkbook.Path).Load().Program);
        var b = PsmJson.Serialize(new ExcelProgramSource(TestWorkbook.Path).Load().Program);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Import_does_not_modify_the_source_workbook()
    {
        var before = SHA256.HashData(File.ReadAllBytes(TestWorkbook.Path));
        var r = new ExcelProgramSource(TestWorkbook.Path).Load();
        var after = SHA256.HashData(File.ReadAllBytes(TestWorkbook.Path));
        Assert.Equal(before, after);
        Assert.Equal(Convert.ToHexString(before).ToLowerInvariant(), r.Program!.Source!.FileSha256);
    }

    [Fact]
    public void Workbook_can_be_read_while_another_process_holds_it_open()
    {
        using var held = new FileStream(TestWorkbook.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.NotNull(new ExcelProgramSource(TestWorkbook.Path).Load().Program);
    }
}
