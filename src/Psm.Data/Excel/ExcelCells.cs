using System.Globalization;
using ClosedXML.Excel;
using Psm.Data.Model;
using Psm.Data.Validation;

namespace Psm.Data.Excel;

/// <summary>Reads ClosedXML cells into <see cref="SourceCell"/>s. Always uses <c>CachedValue</c>, never <c>Value</c>.</summary>
internal static class ExcelCells
{
    /// <summary>
    /// <c>Value</c> would recalculate formulas, and this workbook has a VBA function (<c>findcolor</c>) and
    /// array formulas ClosedXML can't evaluate. <c>CachedValue</c> is what Excel last saved.
    /// </summary>
    public static SourceCell Read(IXLCell cell)
    {
        var v = cell.CachedValue;
        var hasFormula = cell.HasFormula;
        var fill = ReadFill(cell);
        return v.Type switch
        {
            XLDataType.Blank => new SourceCell(CellKind.Blank, HasFormula: hasFormula, Fill: fill),
            XLDataType.Number => new SourceCell(CellKind.Number, v.GetNumber(), NumberFormat: NumberFormatOf(cell), HasFormula: hasFormula, Fill: fill),
            XLDataType.Text => new SourceCell(CellKind.Text, Text: v.GetText(), HasFormula: hasFormula, Fill: fill),
            XLDataType.Boolean => new SourceCell(CellKind.Boolean, Text: v.GetBoolean() ? "TRUE" : "FALSE", HasFormula: hasFormula, Fill: fill),
            XLDataType.DateTime => new SourceCell(CellKind.DateTime, Text: v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), HasFormula: hasFormula, Fill: fill),
            XLDataType.TimeSpan => new SourceCell(CellKind.Text, Text: v.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture), HasFormula: hasFormula, Fill: fill),
            XLDataType.Error => new SourceCell(CellKind.Error, Text: v.GetError().ToString(), HasFormula: hasFormula, Fill: fill),
            _ => SourceCell.Blank,
        };
    }

    /// <summary>
    /// The format code for a numeric cell. ClosedXML gives built-in formats only as an id with an
    /// empty <c>Format</c> (ProgramInfo!A61 "0.00" reads as NumberFormatId 2), so map the ids that matter.
    /// </summary>
    public static string NumberFormatOf(IXLCell cell)
    {
        var nf = cell.Style.NumberFormat;
        if (!string.IsNullOrEmpty(nf.Format)) return nf.Format;
        return BuiltInFormats.TryGetValue(nf.NumberFormatId, out var f) ? f : "General";
    }

    // ECMA-376 Part 1, §18.8.30 built-in number formats (the locale-independent ones).
    private static readonly Dictionary<int, string> BuiltInFormats = new()
    {
        [0] = "General",
        [1] = "0",
        [2] = "0.00",
        [3] = "#,##0",
        [4] = "#,##0.00",
        [9] = "0%",
        [10] = "0.00%",
        [11] = "0.00E+00",
        [12] = "# ?/?",
        [13] = "# ??/??",
        [14] = "mm-dd-yy",
        [15] = "d-mmm-yy",
        [16] = "d-mmm",
        [17] = "mmm-yy",
        [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM",
        [20] = "h:mm",
        [21] = "h:mm:ss",
        [22] = "m/d/yy h:mm",
        [37] = "#,##0 ;(#,##0)",
        [38] = "#,##0 ;[Red](#,##0)",
        [39] = "#,##0.00;(#,##0.00)",
        [40] = "#,##0.00;[Red](#,##0.00)",
        [45] = "mm:ss",
        [46] = "[h]:mm:ss",
        [47] = "mmss.0",
        [48] = "##0.0E+0",
        [49] = "@",
    };

    /// <summary>Solid fill color, or null. Theme colors are resolved without tint.</summary>
    public static RgbColor? ReadFill(IXLCell cell)
    {
        var fill = cell.Style.Fill;
        if (fill.PatternType != XLFillPatternValues.Solid) return null;
        try
        {
            var c = fill.BackgroundColor;
            if (c.ColorType == XLColorType.Theme)
                c = cell.Worksheet.Workbook.Theme.ResolveThemeColor(c.ThemeColor);
            var color = c.Color;
            return color.A == 0 ? null : new RgbColor(color.R, color.G, color.B);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
        {
            // System/indexed colors (e.g. index 64) have no fixed RGB.
            return null;
        }
    }
}
