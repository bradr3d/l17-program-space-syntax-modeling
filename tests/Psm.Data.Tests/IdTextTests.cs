using Psm.Data.Model;
using Psm.Data.Validation;

namespace Psm.Data.Tests;

public class IdTextTests
{
    [Theory]
    [InlineData(1.1, "General", "1.1", IdFormatOutcome.Plain)]
    [InlineData(1.1, null, "1.1", IdFormatOutcome.Plain)]
    [InlineData(7.1, "0.00", "7.10", IdFormatOutcome.Recovered)] // ProgramInfo!A61
    [InlineData(7.12, "0.00", "7.12", IdFormatOutcome.Plain)]
    [InlineData(7.1, "0.0#", "7.1", IdFormatOutcome.Plain)]
    [InlineData(7.1, "#,##0.00", "7.10", IdFormatOutcome.Recovered)]
    [InlineData(7.123, "0.00", "7.12", IdFormatOutcome.Doubtful)]
    [InlineData(7.1, "0.00%", "7.1", IdFormatOutcome.Doubtful)]
    [InlineData(7.1, "\"Rm \"0.00;[Red]-0.00", "7.10", IdFormatOutcome.Recovered)]
    public void Numeric_ids_follow_the_cell_number_format(double value, string? format, string expected, IdFormatOutcome outcome)
    {
        Assert.Equal(expected, IdText.FromNumber(value, format, out var o));
        Assert.Equal(outcome, o);
    }

    [Fact]
    public void Ids_sort_segment_by_segment_as_numbers()
    {
        string[] ids = ["7.10", "1.2", "7.1", "1.1.2", "7.9", "1.1", "1.1.1", "10.1", "7.10.1"];
        Assert.Equal(["1.1", "1.1.1", "1.1.2", "1.2", "7.1", "7.9", "7.10", "7.10.1", "10.1"],
            ids.OrderBy(x => x, ProgramIds.Comparer));
        string[] keys = ["1.1#10", "1.1#2", "1.1.1#0", "1.1#0"];
        Assert.Equal(["1.1#0", "1.1#2", "1.1#10", "1.1.1#0"], keys.OrderBy(x => x, ProgramIds.KeyComparer));
    }

    [Theory]
    [InlineData("1.1", true)]
    [InlineData("1.1.2", true)]
    [InlineData("7.10", true)]
    [InlineData("7", false)]
    [InlineData("0", false)]
    [InlineData("1.", false)]
    [InlineData("A.1", false)]
    public void Id_validity(string id, bool valid) => Assert.Equal(valid, ProgramIds.IsValid(id));

    [Fact]
    public void Header_normalization_ignores_case_spacing_and_trailing_spaces()
    {
        Assert.True(ColumnMap.TryMatch("Total Program SF ", out var c));
        Assert.Equal(ProgramColumn.TotalSf, c);
        Assert.True(ColumnMap.TryMatch("room  qty /units", out c));
        Assert.Equal(ProgramColumn.Qty, c);
        // No loose "contains" matching.
        Assert.False(ColumnMap.TryMatch("Conn", out _));
        Assert.False(ColumnMap.TryMatch("Shared", out _));
    }
}
