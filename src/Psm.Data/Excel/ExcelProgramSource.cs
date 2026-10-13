using System.Globalization;
using System.Security.Cryptography;
using ClosedXML.Excel;
using Psm.Data.Model;
using Psm.Data.Validation;

namespace Psm.Data.Excel;

public sealed record ExcelImportOptions
{
    /// <summary>Worksheet holding the program table. Null → "ProgramInfo", then header search.</summary>
    public string? SheetName { get; init; }
}

/// <summary>
/// Reads the program workbook (.xlsx/.xlsm). The file is opened read-only with
/// <see cref="FileShare.ReadWrite"/> (so it can stay open in Excel), never saved, and its macros never run.
/// </summary>
public sealed class ExcelProgramSource(string path, ExcelImportOptions? options = null) : IProgramSource
{
    public const string DefaultSheet = "ProgramInfo";
    public const string ProjectSheet = "ProjectSheet";
    private const int HeaderSearchRows = 30;

    private readonly ExcelImportOptions _options = options ?? new ExcelImportOptions();

    public ImportResult Load()
    {
        // Read the bytes once: the hash records exactly what was imported, and ClosedXML gets a
        // private in-memory copy, so nothing can ever be written back to the file.
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var wb = new XLWorkbook(new MemoryStream(bytes, writable: false));
        var diags = new List<Diagnostic>();

        var (ws, headerRow) = ResolveSheet(wb, diags);
        var project = ReadProjectInfo(wb);
        var source = new ProgramSource("xlsx", Path.GetFileName(path), ws?.Name, sha, project);
        if (ws is null) return new ImportResult(null, ProgramAssembler.Sorted(diags));

        var table = ReadTable(ws, headerRow);
        var names = ReadDepartmentNames(wb, diags);
        return ProgramAssembler.Assemble(table, source, names, diags);
    }

    /// <summary>
    /// Sheet order: user-supplied name → "ProgramInfo" → first sheet whose header row has
    /// "Dept No" and "Program Element". ("Sheet2", used by the prototype, is only ProgramInfo's
    /// VBA code name; ClosedXML 0.105 does not expose code names, so that step is skipped.)
    /// </summary>
    private (IXLWorksheet? Sheet, int HeaderRow) ResolveSheet(XLWorkbook wb, List<Diagnostic> diags)
    {
        if (_options.SheetName is { } wanted)
        {
            if (wb.TryGetWorksheet(wanted, out var named))
            {
                var hr = FindHeaderRow(named);
                if (hr > 0) return (named, hr);
                diags.Add(new Diagnostic(DiagnosticCodes.MissingColumn, Severity.Error,
                    $"Sheet '{wanted}' has no header row with 'Dept No' and 'Program Element' in its first {HeaderSearchRows} rows.", wanted));
                return (null, 0);
            }
            diags.Add(new Diagnostic(DiagnosticCodes.MissingColumn, Severity.Error,
                $"Sheet '{wanted}' was not found. Sheets in this workbook: {string.Join(", ", wb.Worksheets.Select(w => w.Name))}."));
            return (null, 0);
        }

        if (wb.TryGetWorksheet(DefaultSheet, out var pi) && FindHeaderRow(pi) is > 0 and var piRow)
            return (pi, piRow);

        foreach (var ws in wb.Worksheets)
        {
            var hr = FindHeaderRow(ws);
            if (hr > 0) return (ws, hr);
        }
        diags.Add(new Diagnostic(DiagnosticCodes.MissingColumn, Severity.Error,
            $"No sheet has a program table (a header row with 'Dept No' and 'Program Element'). Sheets: {string.Join(", ", wb.Worksheets.Select(w => w.Name))}."));
        return (null, 0);
    }

    private static int FindHeaderRow(IXLWorksheet ws)
    {
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var r = 1; r <= HeaderSearchRows; r++)
        {
            bool dept = false, name = false;
            for (var c = 1; c <= lastCol; c++)
            {
                var v = ws.Cell(r, c).CachedValue;
                if (!v.IsText) continue;
                if (ColumnMap.TryMatch(v.GetText(), out var col))
                {
                    dept |= col == ProgramColumn.DeptNo;
                    name |= col == ProgramColumn.Name;
                }
            }
            if (dept && name) return r;
        }
        return 0;
    }

    private static SourceTable ReadTable(IXLWorksheet ws, int headerRow)
    {
        var lastCol = ws.Row(headerRow).LastCellUsed()?.Address.ColumnNumber ?? 0;
        var headers = Enumerable.Range(1, lastCol)
            .Select(c => ws.Cell(headerRow, c).CachedValue is { IsText: true } v ? v.GetText() : null)
            .ToList();

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow;
        var rows = new List<SourceRow>();
        for (var r = headerRow + 1; r <= lastRow; r++)
        {
            var cells = new SourceCell[lastCol];
            for (var c = 1; c <= lastCol; c++) cells[c - 1] = ExcelCells.Read(ws.Cell(r, c));
            rows.Add(new SourceRow(r, cells));
        }
        return new SourceTable(ws.Name, headerRow, headers, rows, SupportsFill: true);
    }

    /// <summary>
    /// Department names are on ProjectSheet rows whose Dept No is a whole number
    /// (row 8 "1 Core Curricula - Classrooms"). Dept 8 appears twice on the test workbook
    /// (row 105 "Dining/Student Services", empty; row 122 "Custodial/Building Support", which lists
    /// 8.1–8.5), so the header that actually has rooms under it wins.
    /// </summary>
    private static Dictionary<string, string> ReadDepartmentNames(XLWorkbook wb, List<Diagnostic> diags)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!wb.TryGetWorksheet(ProjectSheet, out var ws)) return names;

        var headers = new List<(string Id, string Name, int Row, bool HasRooms)>();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (var r = 1; r <= lastRow; r++)
        {
            var a = ExcelCells.Read(ws.Cell(r, 1));
            string? id = a.Kind switch
            {
                CellKind.Number => IdText.FromNumber(a.Number, a.NumberFormat, out _),
                CellKind.Text => a.Text!.Trim(),
                _ => null,
            };
            if (id is null) continue;
            if (id.All(char.IsAsciiDigit) && id.Length > 0)
            {
                var name = ws.Cell(r, 2).CachedValue is { IsText: true } b ? b.GetText().Trim() : "";
                if (name.Length > 0) headers.Add((id, name, r, false));
            }
            else if (ProgramIds.IsValid(id) && headers.Count > 0 && headers[^1].Id == ProgramIds.DepartmentOf(id))
            {
                headers[^1] = headers[^1] with { HasRooms = true };
            }
        }

        foreach (var group in headers.GroupBy(h => h.Id))
        {
            var list = group.ToList();
            var pick = list.FirstOrDefault(h => h.HasRooms);
            if (pick.Name is null) pick = list[0];
            names[group.Key] = pick.Name;
            if (list.Count > 1)
            {
                var others = string.Join(", ", list.Where(h => h.Row != pick.Row).Select(h => $"row {h.Row} '{h.Name}'"));
                diags.Add(new Diagnostic(DiagnosticCodes.RepeatedDepartmentHeader, Severity.Info,
                    $"Department {group.Key} is listed more than once on {ProjectSheet}; using row {pick.Row} '{pick.Name}'{(pick.HasRooms ? ", which lists its rooms" : "")}. Also found: {others}.",
                    ProjectSheet, pick.Row, "A"));
            }
        }
        return names;
    }

    /// <summary>Project metadata from ProjectSheet A1:B5 (Project, Client, Date, Revision, Proj No).</summary>
    private static ProjectInfo? ReadProjectInfo(XLWorkbook wb)
    {
        if (!wb.TryGetWorksheet(ProjectSheet, out var ws)) return null;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var r = 1; r <= 6; r++)
        {
            var label = ws.Cell(r, 1).CachedValue is { IsText: true } l ? ColumnMap.Normalize(l.GetText()) : null;
            if (label is null) continue;
            values[label] = Text(ExcelCells.Read(ws.Cell(r, 2)));
        }
        if (values.Count == 0) return null;
        string? Get(params string[] keys) => keys.Select(k => values.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);
        return new ProjectInfo(
            Get("project", "project name"),
            Get("client"),
            Get("date"),
            Get("revision"),
            Get("proj no", "project no", "project number"));
    }

    private static string? Text(SourceCell c) => c.Kind switch
    {
        CellKind.Blank => null,
        CellKind.Number => c.Number.ToString("R", CultureInfo.InvariantCulture),
        _ => string.IsNullOrWhiteSpace(c.Text) ? null : c.Text.Trim(),
    };
}
