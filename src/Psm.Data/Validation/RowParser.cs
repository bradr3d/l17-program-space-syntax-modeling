using System.Globalization;
using System.Text.RegularExpressions;
using Psm.Data.Model;

namespace Psm.Data.Validation;

public enum RowOutcome
{
    Room,
    Blank,
    Divider,
    DepartmentHeader,
    Invalid,
}

/// <summary>How a row's color was decided. Used to report W007/W008 once the whole sheet is read.</summary>
public enum ColorSource
{
    Fill,
    RgbColumn,
    DepartmentDefault,
}

/// <summary>A room row that passed pass 1, plus what pass 2 and the color report need.</summary>
public sealed record ParsedRow(
    RoomType Type,
    int Row,
    ColorSource ColorSource,
    RgbColor? RgbColumnValue,
    string? ConnColumn);

public sealed record RowParseResult(
    RowOutcome Outcome,
    ParsedRow? Room = null,
    string? DepartmentId = null,
    string? DepartmentName = null);

/// <summary>
/// Pass 1: reads one row on its own. Never throws on bad data; problems become diagnostics.
/// A row with a problem in a required field (id, name, qty, net SF) is dropped; a problem in an
/// optional field is reported and the field left empty.
/// </summary>
public sealed partial class RowParser(SourceTable table, ColumnMap map, List<Diagnostic> diagnostics)
{
    private static readonly HashSet<string> SharedTruthy =
        new(["x", "true", "yes", "1", "shared"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeNumber();

    private string Sheet => table.Sheet;

    public RowParseResult Parse(SourceRow row)
    {
        var r = row.RowNumber;

        if (map.Columns.Values.All(i => row[i].IsBlank))
            return new RowParseResult(RowOutcome.Blank);

        // E007: a formula with no cached value. Columns A–H on ProgramInfo are array formulas; a
        // workbook saved by a library that can't recalculate them comes back with every value empty.
        var missingCache = false;
        foreach (var (col, i) in map.Columns)
        {
            var c = row[i];
            if (c.HasFormula && c.Kind == CellKind.Blank)
            {
                missingCache = true;
                Add(DiagnosticCodes.NoCachedValue, Severity.Error,
                    $"Row {r}: '{ColumnMap.DisplayName(col)}' is a formula with no saved value. Open the workbook in Excel and save it, then re-import.",
                    r, col);
            }
        }
        if (missingCache) return new RowParseResult(RowOutcome.Invalid);

        var id = ReadId(row, ProgramColumn.DeptNo, out var idOk);
        var nameCell = row[map[ProgramColumn.Name]];
        var name = CellText(nameCell)?.Trim();
        var nameIsEmptyMarker = string.IsNullOrEmpty(name) || name == "0";

        if (!idOk || !ProgramIds.IsValid(id))
        {
            // Divider rows (ProgramInfo rows 17, 22, 31, …) hold 0 in A–I.
            if (nameIsEmptyMarker) return new RowParseResult(RowOutcome.Divider);

            // "7  Administration/Student Services": a department header, as on ProjectSheet.
            if (id is not null && WholeNumber().IsMatch(id))
                return new RowParseResult(RowOutcome.DepartmentHeader, DepartmentId: id, DepartmentName: name);

            Add(DiagnosticCodes.InvalidId, Severity.Error,
                id is null
                    ? $"Row {r}: '{name}' has no Dept No."
                    : $"Row {r}: Dept No '{id}' is not a valid id (expected numbers separated by dots, like 1.1 or 1.1.2).",
                r, ProgramColumn.DeptNo);
            return new RowParseResult(RowOutcome.Invalid);
        }

        var ok = true;
        if (nameIsEmptyMarker)
        {
            ok = false;
            Add(DiagnosticCodes.BadValue, Severity.Error, $"Row {r}: {id} has no Program Element (room name).", r, ProgramColumn.Name, id);
        }

        var qty = ReadNumber(row, ProgramColumn.Qty, id, required: true, out var qtyOk);
        if (qtyOk && (qty < 0 || qty != Math.Floor(qty!.Value) || qty > int.MaxValue))
        {
            Add(DiagnosticCodes.BadValue, Severity.Error, $"Row {r}: Room Qty for {id} must be a whole number of 0 or more (found {Fmt(qty!.Value)}).", r, ProgramColumn.Qty, id);
            qtyOk = false;
        }
        var net = ReadNumber(row, ProgramColumn.NetSf, id, required: true, out var netOk);
        if (netOk && net < 0)
        {
            Add(DiagnosticCodes.BadValue, Severity.Error, $"Row {r}: Net SF for {id} is negative ({Fmt(net!.Value)}).", r, ProgramColumn.NetSf, id);
            netOk = false;
        }
        ok &= qtyOk && netOk;

        var total = ReadNumber(row, ProgramColumn.TotalSf, id, required: false, out _);
        var stations = ReadWhole(row, ProgramColumn.Stations, id);
        var minWidth = ReadNumber(row, ProgramColumn.MinWidth, id, required: false, out _);
        if (minWidth <= 0) minWidth = null;

        var conn = ReadId(row, ProgramColumn.ConnPrimary, out _);
        if (conn is "0" or "") conn = null; // 0 or blank = no connection

        var floor = ReadFloor(row, id);
        var shared = map.Has(ProgramColumn.SharedAccess) && SharedTruthy.Contains(CellText(row[map[ProgramColumn.SharedAccess]])?.Trim() ?? "");
        var notes = ReadNotes(row);
        var aspect = ReadAspect(row);
        var (color, colorSource, rgbValue) = ReadColor(row, id);

        if (!ok) return new RowParseResult(RowOutcome.Invalid);

        var q = (int)qty!.Value;
        var absorbed = q == 0;
        if (absorbed)
            Add(DiagnosticCodes.Absorbed, Severity.Warning,
                $"Row {r}: {id} {name} has Qty 0, so it is treated as absorbed into other spaces and gets no rooms.",
                r, ProgramColumn.Qty, id);

        var type = new RoomType(
            Id: id!,
            ParentId: ProgramIds.ParentOf(id!),
            DepartmentId: ProgramIds.DepartmentOf(id!),
            Name: name!,
            Qty: q,
            NetSf: net!.Value,
            TotalSf: total,
            Stations: stations,
            ConnPrimary: conn,
            FloorPref: floor,
            Color: color,
            SharedAccess: shared,
            MinWidthFt: minWidth,
            Aspect: aspect,
            Absorbed: absorbed,
            Notes: notes,
            Source: new CellRef(Sheet, r));
        return new RowParseResult(RowOutcome.Room, new ParsedRow(type, r, colorSource, rgbValue, conn));
    }

    // ---- field readers ------------------------------------------------------------------------

    /// <summary>Reads an id cell. Numbers use the cell's number format (see <see cref="IdText"/>).</summary>
    private string? ReadId(SourceRow row, ProgramColumn col, out bool ok)
    {
        ok = true;
        if (!map.Has(col)) return null;
        var c = row[map[col]];
        switch (c.Kind)
        {
            case CellKind.Blank:
                return null;
            case CellKind.Number:
                var text = IdText.FromNumber(c.Number, c.NumberFormat, out var outcome);
                if (outcome != IdFormatOutcome.Plain && ProgramIds.IsValid(text))
                {
                    var plain = c.Number.ToString("R", CultureInfo.InvariantCulture);
                    Add(DiagnosticCodes.IdStoredAsNumber,
                        outcome == IdFormatOutcome.Recovered ? Severity.Info : Severity.Warning,
                        outcome == IdFormatOutcome.Recovered
                            ? $"Row {row.RowNumber}: {ColumnMap.DisplayName(col)} is stored as the number {plain}; read as '{text}' from its number format. Consider typing it as text ('{text})."
                            : $"Row {row.RowNumber}: {ColumnMap.DisplayName(col)} is stored as the number {plain} and its number format ('{c.NumberFormat}') may hide or change digits; read as '{text}'. Type it as text to be sure.",
                        row.RowNumber, col);
                }
                return text;
            default:
                var t = CellText(c)?.Trim();
                return string.IsNullOrEmpty(t) ? null : t;
        }
    }

    private double? ReadNumber(SourceRow row, ProgramColumn col, string? id, bool required, out bool ok)
    {
        ok = true;
        if (!map.Has(col)) { ok = !required; return null; }
        var c = row[map[col]];
        if (c.Kind == CellKind.Number) return c.Number;
        if (c.IsBlank)
        {
            if (!required) return null;
            ok = false;
            Add(DiagnosticCodes.BadValue, Severity.Error, $"Row {row.RowNumber}: {ColumnMap.DisplayName(col)} for {id} is blank.", row.RowNumber, col, id);
            return null;
        }
        var text = CellText(c)!.Trim().Replace(",", "");
        if (c.Kind == CellKind.Text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && double.IsFinite(v))
            return v;
        ok = false;
        Add(DiagnosticCodes.BadValue, Severity.Error,
            $"Row {row.RowNumber}: {ColumnMap.DisplayName(col)} for {id} should be a number but is '{CellText(c)}'.",
            row.RowNumber, col, id);
        return null;
    }

    private int? ReadWhole(SourceRow row, ProgramColumn col, string? id)
    {
        var v = ReadNumber(row, col, id, required: false, out var ok);
        if (!ok || v is null) return null;
        if (v < 0 || v != Math.Floor(v.Value) || v > int.MaxValue)
        {
            Add(DiagnosticCodes.BadValue, Severity.Error,
                $"Row {row.RowNumber}: {ColumnMap.DisplayName(col)} for {id} must be a whole number of 0 or more (found {Fmt(v.Value)}).",
                row.RowNumber, col, id);
            return null;
        }
        return (int)v.Value;
    }

    /// <summary>
    /// Flr is a preference (prototype handoff §2.3): blank, non-numeric or ≤ 0 → no preference.
    /// A positive fraction (1.5) is reported as E005 because no floor can be inferred from it.
    /// </summary>
    private int? ReadFloor(SourceRow row, string? id)
    {
        if (!map.Has(ProgramColumn.Floor)) return null;
        var c = row[map[ProgramColumn.Floor]];
        double v;
        if (c.Kind == CellKind.Number) v = c.Number;
        else if (c.Kind == CellKind.Text && double.TryParse(c.Text!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) v = p;
        else return null;
        if (!double.IsFinite(v) || v <= 0) return null;
        if (v != Math.Floor(v) || v > int.MaxValue)
        {
            Add(DiagnosticCodes.BadValue, Severity.Error, $"Row {row.RowNumber}: Flr for {id} must be a whole floor number (found {Fmt(v)}).", row.RowNumber, ProgramColumn.Floor, id);
            return null;
        }
        return (int)v;
    }

    /// <summary>Notes holds 0 when empty (array formula mirroring a blank cell).</summary>
    private string? ReadNotes(SourceRow row)
    {
        if (!map.Has(ProgramColumn.Notes)) return null;
        var c = row[map[ProgramColumn.Notes]];
        if (c.Kind == CellKind.Number && c.Number == 0) return null;
        var t = CellText(c)?.Trim();
        return string.IsNullOrEmpty(t) || t == "0" ? null : t;
    }

    /// <summary>Aspect Ratio is text ("1 to 1"). Its number format is ignored (O2 carries a stray h:mm).</summary>
    private string? ReadAspect(SourceRow row)
    {
        if (!map.Has(ProgramColumn.Aspect)) return null;
        var t = CellText(row[map[ProgramColumn.Aspect]])?.Trim();
        return string.IsNullOrEmpty(t) || t.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : t;
    }

    /// <summary>
    /// Color = fill of the Color column (K). Color RGB (L) is a VBA function whose cached value is a
    /// stale "255, 255, 255" on every row of the test workbook, so it is only a fallback.
    /// Department default is filled in later by the assembler.
    /// </summary>
    private (RgbColor Color, ColorSource Source, RgbColor? RgbValue) ReadColor(SourceRow row, string? id)
    {
        var fill = map.Has(ProgramColumn.Color) ? row[map[ProgramColumn.Color]].Fill : null;

        RgbColor? rgb = null;
        if (map.Has(ProgramColumn.ColorRgb))
        {
            var c = row[map[ProgramColumn.ColorRgb]];
            var t = CellText(c);
            if (!c.IsBlank)
            {
                if (RgbColor.TryParse(t, out var parsed)) rgb = parsed;
                else if (fill is null)
                    Add(DiagnosticCodes.BadValue, Severity.Error,
                        $"Row {row.RowNumber}: Color RGB for {id} should look like '168, 174, 73' but is '{t}'.",
                        row.RowNumber, ProgramColumn.ColorRgb, id);
            }
        }

        if (fill is { } f) return (f, ColorSource.Fill, rgb);
        if (rgb is { } g) return (g, ColorSource.RgbColumn, rgb);
        return (RgbColor.MidGrey, ColorSource.DepartmentDefault, null);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string? CellText(SourceCell c) => c.Kind switch
    {
        CellKind.Blank => null,
        CellKind.Number => c.Number.ToString("R", CultureInfo.InvariantCulture),
        _ => c.Text,
    };

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private void Add(string code, Severity sev, string message, int row, ProgramColumn col, string? typeId = null) =>
        diagnostics.Add(new Diagnostic(code, sev, message, Sheet, row, map.Letter(col), typeId));
}
