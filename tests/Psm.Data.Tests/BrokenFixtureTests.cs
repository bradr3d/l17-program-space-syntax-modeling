using ClosedXML.Excel;
using Psm.Data.Excel;
using Psm.Data.Model;
using Psm.Data.Validation;

namespace Psm.Data.Tests;

/// <summary>One small generated workbook per diagnostic code. None of them may throw.</summary>
public class BrokenFixtureTests
{
    private static WorkbookBuilder Base() => new WorkbookBuilder()
        .Room("1.1", "Classroom", 2, 850, "1.2")
        .Room("1.1.1", "Restroom", 2, 50, "1.1", shared: true)
        .Room("1.2", "Classroom", 2, 850, "1.1");

    [Fact]
    public void Clean_fixture_has_no_warnings_or_errors()
    {
        var r = Base().Import();
        Assert.True(r.Ok);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity != Severity.Info && d.Code != DiagnosticCodes.RgbDisagreesWithFill);
        Assert.Equal(6, r.Program!.Instances.Count);
        Assert.Equal(6, r.Program.Edges.Count);
    }

    [Fact]
    public void E001_duplicate_id()
    {
        var r = Base().Room("1.2", "Another", 1, 100, "1.1").Import();
        var d = Assert.Single(r.WithCode(DiagnosticCodes.DuplicateId));
        Assert.Equal((5, "A", Severity.Error), (d.Row!.Value, d.Column!, d.Severity));
        Assert.False(r.Ok);
        Assert.Equal(1, r.Program!.Summary!.InvalidRowsSkipped);
        Assert.Equal("Classroom", r.Program.FindType("1.2")!.Name); // first row keeps the id
    }

    [Fact]
    public void E001_is_not_raised_when_the_number_format_separates_7_1_and_7_10()
    {
        var b = new WorkbookBuilder()
            .Room(7.1, "Reception", 1, 100)
            .Room(7.1, "CAE Workroom", 1, 500);
        b.Cell(3, 1).Style.NumberFormat.NumberFormatId = 2; // built-in "0.00", as on ProgramInfo!A61
        var r = b.Import();
        Assert.Empty(r.WithCode(DiagnosticCodes.DuplicateId));
        Assert.Equal(["7.1", "7.10"], r.Program!.RoomTypes.Select(t => t.Id));
    }

    [Fact]
    public void E002_required_column_missing()
    {
        string[] headers = ["Dept No", "Program Element", "Room Qty/ Units", "Net SF/ Units"]; // no Conn. Primary
        var b = new WorkbookBuilder(headers: headers).Row("1.1", "Classroom", 1, 100);
        var r = b.Import();
        Assert.Null(r.Program);
        var d = Assert.Single(r.WithCode(DiagnosticCodes.MissingColumn));
        Assert.Contains("Conn. Primary", d.Message);
    }

    [Fact]
    public void E002_when_no_sheet_has_a_program_table()
    {
        var b = new WorkbookBuilder("Notes", headers: ["Hello"]);
        var r = b.Import();
        Assert.Null(r.Program);
        Assert.Single(r.WithCode(DiagnosticCodes.MissingColumn));
    }

    [Fact]
    public void E003_connection_to_unknown_id()
    {
        var r = Base().Room("4.7", "Lounge", 1, 200, "4.8").Import();
        var d = Assert.Single(r.WithCode(DiagnosticCodes.UnknownConnection));
        Assert.Equal("Row 5: Conn. Primary '4.8' does not match any Dept No.", d.Message);
        Assert.Equal("I", d.Column);
        Assert.Equal(1, r.Program!.Summary!.UnresolvedRoomTypes);
        Assert.DoesNotContain(r.Program.Edges, e => e.FromKey.StartsWith("4.7#"));
    }

    [Fact]
    public void E004_child_without_parent()
    {
        var r = Base().Room("2.1.1", "Storage", 1, 100, "1.1").Import();
        var d = Assert.Single(r.WithCode(DiagnosticCodes.MissingParent));
        Assert.Equal("2.1.1", d.TypeId);
    }

    [Fact]
    public void E005_text_in_qty_and_negative_area()
    {
        var r = Base()
            .Room("2.1", "Art", "two", 100, "1.1")
            .Room("2.2", "Kiln", 1, -5, "1.1")
            .Import();
        var e005 = r.WithCode(DiagnosticCodes.BadValue);
        Assert.Equal([(5, "C"), (6, "D")], e005.Select(d => (d.Row!.Value, d.Column!)));
        Assert.Null(r.Program!.FindType("2.1"));
        Assert.Null(r.Program.FindType("2.2"));
        Assert.Equal(2, r.Program.Summary!.InvalidRowsSkipped);
    }

    [Fact]
    public void E005_bad_rgb_when_there_is_no_fill()
    {
        var r = Base().Room("1.3", "Classroom", 1, 850, "1.1", rgb: "red", noFill: true).Import();
        var d = Assert.Single(r.WithCode(DiagnosticCodes.BadValue));
        Assert.Equal("L", d.Column);
    }

    [Fact]
    public void E006_data_row_with_invalid_id()
    {
        var r = Base().Room("abc", "Lounge", 1, 200, "1.1").Room(null!, "Unnumbered", 1, 100).Import();
        Assert.Equal([5, 6], r.WithCode(DiagnosticCodes.InvalidId).Select(d => d.Row!.Value));
    }

    [Fact]
    public void E007_formula_without_cached_value()
    {
        // ClosedXML writes the formula with no cached result: what a library save does to the
        // workbook's array formulas (handoff §2.3).
        var b = Base();
        b.Cell(2, 1).FormulaA1 = "ProjectSheet!A9";
        var r = b.Import();
        var d = Assert.Single(r.WithCode(DiagnosticCodes.NoCachedValue));
        Assert.Equal((2, "A"), (d.Row!.Value, d.Column!));
        Assert.Null(r.Program!.FindType("1.1"));
    }

    [Fact]
    public void Divider_and_blank_rows_are_skipped_and_counted()
    {
        var r = Base()
            .Row(0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
            .Row()
            .Row(null, null, null, null, null, null, null, "stray note")
            .Import();
        Assert.True(r.Ok);
        var s = r.Program!.Summary!;
        Assert.Equal(2, s.DividerRowsSkipped);
        Assert.Equal(1, s.BlankRowsSkipped);
        Assert.Equal(3, s.RoomTypes);
        // The "stray note" row has no id and no name: a divider/empty-marker row, not an error.
        Assert.Equal(s.RowsRead, s.RoomTypes + s.DividerRowsSkipped + s.BlankRowsSkipped + s.InvalidRowsSkipped + s.DepartmentHeaderRows);
    }

    [Fact]
    public void W001_qty_times_net_differs_from_total()
    {
        var r = Base().Room("7.1", "Reception", 1, 17, "1.1", total: 298).Import();
        Assert.Equal("7.1", Assert.Single(r.WithCode(DiagnosticCodes.TotalMismatch)).TypeId);
        Assert.Equal(17, r.Program!.FindType("7.1")!.NetSf);
    }

    [Fact]
    public void W002_and_W003_absorbed_target()
    {
        var r = Base()
            .Room("7.11", "Homework Lab", 0, 500, "1.1")
            .Room("7.11.1", "Tutoring", 6, 100, "7.11")
            .Import();
        Assert.Equal("7.11", Assert.Single(r.WithCode(DiagnosticCodes.Absorbed)).TypeId);
        Assert.Equal("7.11.1", Assert.Single(r.WithCode(DiagnosticCodes.ConnectionToAbsorbed)).TypeId);
        Assert.Equal("7.11.1", Assert.Single(r.WithCode(DiagnosticCodes.ChildQtyExceedsParent)).TypeId);
        Assert.Equal(6, r.Program!.Summary!.EdgesSkipped);
        Assert.True(r.Program.FindType("7.11")!.Absorbed);
    }

    [Fact]
    public void W006_area_below_min_width_squared()
    {
        var r = Base().Room("1.3", "Closet", 1, 20, "1.1", minWidth: 5).Import();
        Assert.Equal("1.3", Assert.Single(r.WithCode(DiagnosticCodes.AreaBelowMinWidth)).TypeId);
    }

    [Fact]
    public void W007_no_fill_falls_back_to_rgb_then_department()
    {
        var r = Base()
            .Room("1.3", "From L", 1, 100, "1.1", rgb: "10, 20, 30", noFill: true)
            .Room("1.4", "From dept", 1, 100, "1.1", rgb: null, noFill: true)
            .Import();
        Assert.Equal(["1.3", "1.4"], r.WithCode(DiagnosticCodes.NoFillColor).Select(d => d.TypeId));
        Assert.Equal(new RgbColor(10, 20, 30), r.Program!.FindType("1.3")!.Color);
        Assert.Equal(new RgbColor(168, 174, 73), r.Program.FindType("1.4")!.Color);
    }

    [Fact]
    public void W008_per_row_when_only_some_rows_disagree()
    {
        var r = new WorkbookBuilder()
            .Room("1.1", "A", 1, 100, rgb: "168, 174, 73")
            .Room("1.2", "B", 1, 100, rgb: "1, 2, 3")
            .Import();
        Assert.Equal("1.2", Assert.Single(r.WithCode(DiagnosticCodes.RgbDisagreesWithFill)).TypeId);
    }

    [Fact]
    public void I002_unknown_column()
    {
        var headers = WorkbookBuilder.StandardHeaders.Append("Ceiling Height").ToArray();
        var r = new WorkbookBuilder(headers: headers).Room("1.1", "Classroom", 1, 100).Import();
        var d = Assert.Single(r.WithCode(DiagnosticCodes.UnknownColumn));
        Assert.Equal("P", d.Column);
    }

    [Fact]
    public void Theme_fill_color_is_resolved()
    {
        var b = new WorkbookBuilder().Room("1.1", "Classroom", 1, 100, fill: XLColor.FromTheme(XLThemeColor.Accent1));
        var r = b.Import();
        Assert.NotEqual(RgbColor.MidGrey, r.Program!.FindType("1.1")!.Color);
        Assert.Empty(r.WithCode(DiagnosticCodes.NoFillColor));
    }

    [Fact]
    public void Sheet_found_by_header_search_when_not_named_ProgramInfo()
    {
        var b = new WorkbookBuilder("Sheet2").Room("1.1", "Classroom", 1, 100);
        var r = b.Import();
        Assert.Equal("Sheet2", r.Program!.Source!.Sheet);
    }

    [Fact]
    public void User_supplied_sheet_that_does_not_exist_is_reported_not_thrown()
    {
        var r = Base().Import(new ExcelImportOptions { SheetName = "Nope" });
        Assert.Null(r.Program);
        Assert.Contains("Nope", Assert.Single(r.WithCode(DiagnosticCodes.MissingColumn)).Message);
    }

    [Fact]
    public void Missing_file_is_a_real_failure()
    {
        Assert.ThrowsAny<IOException>(() => new ExcelProgramSource("/nonexistent/program.xlsx").Load());
    }
}
