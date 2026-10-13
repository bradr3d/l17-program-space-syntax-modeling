using System.Text;
using Psm.Data.Model;

namespace Psm.Data.Validation;

public enum CellKind
{
    Blank,
    Number,
    Text,
    Boolean,
    DateTime,
    Error,
}

/// <summary>
/// One cell as read from a source, before any interpretation. Excel cells carry their cached value,
/// resolved number format and fill; CSV cells are always text.
/// </summary>
/// <param name="Text">Text value; for Boolean/DateTime/Error kinds an invariant rendering of the value.</param>
/// <param name="NumberFormat">Excel number format code for numeric cells ("0.00", "General"), resolved from built-in ids.</param>
/// <param name="HasFormula">The cell holds a formula (its value is the cached result).</param>
/// <param name="Fill">Solid fill color, if any.</param>
public sealed record SourceCell(
    CellKind Kind,
    double Number = 0,
    string? Text = null,
    string? NumberFormat = null,
    bool HasFormula = false,
    RgbColor? Fill = null)
{
    public static readonly SourceCell Blank = new(CellKind.Blank);

    public static SourceCell FromText(string? text) =>
        string.IsNullOrEmpty(text) ? Blank : new SourceCell(CellKind.Text, Text: text);

    public static SourceCell FromNumber(double value, string? numberFormat = null) =>
        new(CellKind.Number, value, NumberFormat: numberFormat);

    public bool IsBlank => Kind == CellKind.Blank || (Kind == CellKind.Text && string.IsNullOrWhiteSpace(Text));
}

/// <summary>A data row. <see cref="Cells"/> is indexed by 0-based column.</summary>
public sealed record SourceRow(int RowNumber, IReadOnlyList<SourceCell> Cells)
{
    public SourceCell this[int column] =>
        column >= 0 && column < Cells.Count ? Cells[column] : SourceCell.Blank;
}

/// <summary>A program table read from any source, ready for validation.</summary>
/// <param name="SupportsFill">True when the source can carry cell fills (Excel), so a missing fill is worth a warning.</param>
public sealed record SourceTable(
    string Sheet,
    int HeaderRow,
    IReadOnlyList<string?> Headers,
    IReadOnlyList<SourceRow> Rows,
    bool SupportsFill);

public static class ColumnLetters
{
    /// <summary>0 → "A", 25 → "Z", 26 → "AA".</summary>
    public static string FromIndex(int index)
    {
        var sb = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }
}
