using System.Text;
using System.Text.RegularExpressions;
using Psm.Data.Model;

namespace Psm.Data.Validation;

public enum ProgramColumn
{
    DeptNo,
    Name,
    Qty,
    NetSf,
    TotalSf,
    Stations,
    TotalStations,
    Notes,
    ConnPrimary,
    Floor,
    Color,
    ColorRgb,
    SharedAccess,
    MinWidth,
    Aspect,
}

/// <summary>
/// Maps header text to program columns by exact normalized name or an explicit alias.
/// There is deliberately no "contains" fallback: the prototype's loose match let "Conn" or
/// "Shared" hit the wrong column.
/// </summary>
public sealed partial class ColumnMap
{
    public static readonly IReadOnlyList<ProgramColumn> Required =
    [
        ProgramColumn.DeptNo, ProgramColumn.Name, ProgramColumn.Qty, ProgramColumn.NetSf, ProgramColumn.ConnPrimary,
    ];

    /// <summary>Display name used in messages, matching the workbook header.</summary>
    public static string DisplayName(ProgramColumn c) => c switch
    {
        ProgramColumn.DeptNo => "Dept No",
        ProgramColumn.Name => "Program Element",
        ProgramColumn.Qty => "Room Qty/ Units",
        ProgramColumn.NetSf => "Net SF/ Units",
        ProgramColumn.TotalSf => "Total Program SF",
        ProgramColumn.Stations => "Design Capacity / Student Stations",
        ProgramColumn.TotalStations => "Total Stations",
        ProgramColumn.Notes => "Notes/Comments",
        ProgramColumn.ConnPrimary => "Conn. Primary",
        ProgramColumn.Floor => "Flr",
        ProgramColumn.Color => "Color",
        ProgramColumn.ColorRgb => "Color RGB",
        ProgramColumn.SharedAccess => "Shared Access",
        ProgramColumn.MinWidth => "Room Width Min.",
        ProgramColumn.Aspect => "Aspect Ratio",
        _ => c.ToString(),
    };

    // Aliases are compared after Normalize(), so case, repeated spaces and spaces around '/' don't matter.
    private static readonly Dictionary<string, ProgramColumn> Aliases = BuildAliases(new()
    {
        [ProgramColumn.DeptNo] = ["Dept No", "Dept No.", "Dept. No.", "Department No"],
        [ProgramColumn.Name] = ["Program Element", "Room Name"],
        [ProgramColumn.Qty] = ["Room Qty/ Units", "Room Qty", "Qty"],
        [ProgramColumn.NetSf] = ["Net SF/ Units", "Net SF", "Net Area"],
        [ProgramColumn.TotalSf] = ["Total Program SF", "Total SF"],
        [ProgramColumn.Stations] = ["Design Capacity / Student Stations", "Student Stations", "Design Capacity"],
        [ProgramColumn.TotalStations] = ["Total Stations"],
        [ProgramColumn.Notes] = ["Notes/Comments", "Notes", "Comments"],
        [ProgramColumn.ConnPrimary] = ["Conn. Primary", "Conn Primary", "Connection Primary", "Primary Connection"],
        [ProgramColumn.Floor] = ["Flr", "Floor", "Floor Preference"],
        [ProgramColumn.Color] = ["Color", "Colour"],
        [ProgramColumn.ColorRgb] = ["Color RGB", "Colour RGB", "RGB"],
        [ProgramColumn.SharedAccess] = ["Shared Access"],
        [ProgramColumn.MinWidth] = ["Room Width Min.", "Room Width Min", "Min Room Width", "Min Width"],
        [ProgramColumn.Aspect] = ["Aspect Ratio"],
    });

    private readonly Dictionary<ProgramColumn, int> _index = new();

    public IReadOnlyDictionary<ProgramColumn, int> Columns => _index;

    public bool Has(ProgramColumn c) => _index.ContainsKey(c);

    /// <summary>0-based column index, or -1 if absent.</summary>
    public int this[ProgramColumn c] => _index.TryGetValue(c, out var i) ? i : -1;

    public string Letter(ProgramColumn c) => Has(c) ? ColumnLetters.FromIndex(this[c]) : "?";

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*/\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SlashSpacing();

    /// <summary>Trim, lower-case, collapse whitespace, drop spaces around '/'. "Total Program SF " → "total program sf".</summary>
    public static string Normalize(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "";
        var s = Whitespace().Replace(header.Trim(), " ");
        s = SlashSpacing().Replace(s, "/");
        return s.ToLowerInvariant();
    }

    public static bool TryMatch(string? header, out ProgramColumn column) =>
        Aliases.TryGetValue(Normalize(header), out column);

    /// <summary>
    /// Maps headers. Reports E002 for each missing required column and I002 for unknown or repeated
    /// columns. Returns null when a required column is missing.
    /// </summary>
    public static ColumnMap? Build(SourceTable table, List<Diagnostic> diagnostics)
    {
        var map = new ColumnMap();
        for (var i = 0; i < table.Headers.Count; i++)
        {
            var h = table.Headers[i];
            if (string.IsNullOrWhiteSpace(h)) continue;
            var letter = ColumnLetters.FromIndex(i);
            if (!TryMatch(h, out var col))
            {
                diagnostics.Add(new Diagnostic(DiagnosticCodes.UnknownColumn, Severity.Info,
                    $"Column {letter} '{h.Trim()}' is not a program column and was ignored.",
                    table.Sheet, table.HeaderRow, letter));
                continue;
            }
            if (!map._index.TryAdd(col, i))
            {
                diagnostics.Add(new Diagnostic(DiagnosticCodes.UnknownColumn, Severity.Info,
                    $"Column {letter} '{h.Trim()}' repeats '{DisplayName(col)}' (already column {map.Letter(col)}) and was ignored.",
                    table.Sheet, table.HeaderRow, letter));
            }
        }

        var ok = true;
        foreach (var req in Required)
        {
            if (map.Has(req)) continue;
            ok = false;
            diagnostics.Add(new Diagnostic(DiagnosticCodes.MissingColumn, Severity.Error,
                $"Required column '{DisplayName(req)}' was not found in the header row (row {table.HeaderRow}).",
                table.Sheet, table.HeaderRow));
        }
        return ok ? map : null;
    }

    private static Dictionary<string, ProgramColumn> BuildAliases(Dictionary<ProgramColumn, string[]> src)
    {
        var d = new Dictionary<string, ProgramColumn>(StringComparer.Ordinal);
        foreach (var (col, names) in src)
            foreach (var n in names)
                d.Add(Normalize(n), col);
        return d;
    }
}
