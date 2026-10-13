namespace Psm.Data.Model;

/// <summary>Where a value came from in the source file. Row is 1-based; Column is a letter ("A").</summary>
public sealed record CellRef(string Sheet, int Row, string? Column = null)
{
    public override string ToString() =>
        Column is null ? $"{Sheet}!{Row}" : $"{Sheet}!{Column}{Row}";
}
