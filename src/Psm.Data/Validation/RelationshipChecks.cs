using System.Globalization;
using Psm.Data.Model;

namespace Psm.Data.Validation;

/// <summary>Result of pass 2: the rows kept and which Conn. Primary links can be wired.</summary>
public sealed record RelationshipResult(
    IReadOnlyList<ParsedRow> Rows,
    int DuplicateRowsDropped,
    IReadOnlySet<string> UnwirableSources);

/// <summary>Pass 2: checks across rows, run only over rows that passed pass 1.</summary>
public static class RelationshipChecks
{
    public static RelationshipResult Run(IReadOnlyList<ParsedRow> parsed, string sheet, ColumnMap map, List<Diagnostic> diagnostics)
    {
        void Add(string code, Severity sev, string msg, int? row, ProgramColumn? col, string? typeId) =>
            diagnostics.Add(new Diagnostic(code, sev, msg, sheet, row, col is { } c ? map.Letter(c) : null, typeId));

        // E001: duplicates that survive the number-format rule. The first row keeps the id.
        var byId = new Dictionary<string, ParsedRow>(StringComparer.Ordinal);
        var kept = new List<ParsedRow>();
        var dropped = 0;
        foreach (var p in parsed)
        {
            if (byId.TryGetValue(p.Type.Id, out var first))
            {
                dropped++;
                Add(DiagnosticCodes.DuplicateId, Severity.Error,
                    $"Row {p.Row}: Dept No '{p.Type.Id}' ({p.Type.Name}) is already used by row {first.Row} ({first.Type.Name}). Each room needs its own Dept No.",
                    p.Row, ProgramColumn.DeptNo, p.Type.Id);
                continue;
            }
            byId.Add(p.Type.Id, p);
            kept.Add(p);
        }

        var unwirable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in kept)
        {
            var t = p.Type;

            // E004 / W005: x.y.z rows and their x.y parent.
            if (t.ParentId is { } pid)
            {
                if (!byId.TryGetValue(pid, out var parent))
                {
                    Add(DiagnosticCodes.MissingParent, Severity.Error,
                        $"Row {p.Row}: {t.Id} {t.Name} has no parent row {pid}.",
                        p.Row, ProgramColumn.DeptNo, t.Id);
                }
                else if (t.Qty > parent.Type.Qty)
                {
                    Add(DiagnosticCodes.ChildQtyExceedsParent, Severity.Warning,
                        $"Row {p.Row}: {t.Id} {t.Name} has Qty {t.Qty}, more than its parent {pid} {parent.Type.Name} (Qty {parent.Type.Qty}).",
                        p.Row, ProgramColumn.Qty, t.Id);
                }
            }

            // W001: Qty × Net SF vs Total Program SF (e.g. 7.1 Reception: 1 × 17 vs 298, "5% cap").
            if (t.TotalSf is { } total && Math.Abs(t.Qty * t.NetSf - total) > 0.5)
            {
                Add(DiagnosticCodes.TotalMismatch, Severity.Warning,
                    $"Row {p.Row}: {t.Id} {t.Name}: Qty × Net SF = {N(t.Qty)} × {N(t.NetSf)} = {N(t.Qty * t.NetSf)}, but Total Program SF is {N(total)}. Rooms use the Net SF ({N(t.NetSf)} SF each).",
                    p.Row, ProgramColumn.TotalSf, t.Id);
            }

            // W006: area too small for the minimum width.
            if (t.MinWidthFt is { } w && t.NetSf < w * w)
            {
                Add(DiagnosticCodes.AreaBelowMinWidth, Severity.Warning,
                    $"Row {p.Row}: {t.Id} {t.Name} is {N(t.NetSf)} SF, smaller than a square of its minimum width ({N(w)} ft × {N(w)} ft = {N(w * w)} SF).",
                    p.Row, ProgramColumn.MinWidth, t.Id);
            }

            // E003 / W003: Conn. Primary target.
            if (t.ConnPrimary is not { } target) continue;
            if (target == t.Id)
            {
                unwirable.Add(t.Id);
                Add(DiagnosticCodes.UnknownConnection, Severity.Error,
                    $"Row {p.Row}: Conn. Primary of {t.Id} {t.Name} points to itself.",
                    p.Row, ProgramColumn.ConnPrimary, t.Id);
            }
            else if (!byId.TryGetValue(target, out var tgt))
            {
                unwirable.Add(t.Id);
                Add(DiagnosticCodes.UnknownConnection, Severity.Error,
                    $"Row {p.Row}: Conn. Primary '{target}' does not match any Dept No.",
                    p.Row, ProgramColumn.ConnPrimary, t.Id);
            }
            else if (tgt.Type.Absorbed && !t.Absorbed)
            {
                unwirable.Add(t.Id);
                Add(DiagnosticCodes.ConnectionToAbsorbed, Severity.Warning,
                    $"Row {p.Row}: {t.Id} {t.Name} connects to {target} {tgt.Type.Name}, which has Qty 0 (absorbed). Its {t.Qty} room(s) get no adjacency.",
                    p.Row, ProgramColumn.ConnPrimary, t.Id);
            }
        }

        return new RelationshipResult(kept, dropped, unwirable);
    }

    /// <summary>
    /// I001: cycles in the Conn. Primary graph (e.g. classrooms 1.1 → 1.2 → … → 1.5 → 1.1).
    /// Each room type has at most one target, so every cycle is found by walking forward.
    /// </summary>
    public static void ReportCycles(IReadOnlyDictionary<string, string> next, IReadOnlyDictionary<string, int> rows,
        string sheet, ColumnMap map, List<Diagnostic> diagnostics)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on current walk, 2 = done
        foreach (var start in next.Keys.OrderBy(k => k, ProgramIds.Comparer))
        {
            if (state.ContainsKey(start)) continue;
            var path = new List<string>();
            string? cur = start;
            while (cur is not null && !state.ContainsKey(cur))
            {
                state[cur] = 1;
                path.Add(cur);
                cur = next.TryGetValue(cur, out var n) ? n : null;
            }
            if (cur is not null && state[cur] == 1)
            {
                var cycle = path.Skip(path.IndexOf(cur)).ToList();
                var min = cycle.OrderBy(k => k, ProgramIds.Comparer).First();
                var at = cycle.IndexOf(min);
                var ordered = cycle.Skip(at).Concat(cycle.Take(at)).Append(min);
                diagnostics.Add(new Diagnostic(DiagnosticCodes.AdjacencyCycle, Severity.Info,
                    $"Row {rows[min]}: Conn. Primary links form a loop: {string.Join(" → ", ordered)}.",
                    sheet, rows[min], map.Letter(ProgramColumn.ConnPrimary), min));
            }
            foreach (var p in path) state[p] = 2;
        }
    }

    private static string N(double v) => v.ToString("#,##0.##", CultureInfo.InvariantCulture);
}
