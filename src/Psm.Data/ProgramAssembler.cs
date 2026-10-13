using Psm.Data.Model;
using Psm.Data.Serialization;
using Psm.Data.Validation;

namespace Psm.Data;

/// <summary>
/// Turns a <see cref="SourceTable"/> from any source into a <see cref="BuildingProgram"/>:
/// pass 1 (rows), pass 2 (relationships), colors, instance explosion, pairwise edges, summary, hash.
/// </summary>
public static class ProgramAssembler
{
    /// <param name="departmentNames">Names keyed by department id (e.g. from ProjectSheet). Header rows in the table win.</param>
    /// <param name="priorDiagnostics">Findings from reading the source (sheet resolution, ProjectSheet, …).</param>
    public static ImportResult Assemble(
        SourceTable table,
        ProgramSource source,
        IReadOnlyDictionary<string, string>? departmentNames = null,
        IEnumerable<Diagnostic>? priorDiagnostics = null)
    {
        var diags = new List<Diagnostic>(priorDiagnostics ?? []);
        var map = ColumnMap.Build(table, diags);
        if (map is null) return new ImportResult(null, Sorted(diags));

        // ---- pass 1 -----------------------------------------------------------------------------
        var parser = new RowParser(table, map, diags);
        var parsed = new List<ParsedRow>();
        var names = new Dictionary<string, string>(departmentNames ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        int blank = 0, divider = 0, invalid = 0, deptRows = 0;
        foreach (var row in table.Rows)
        {
            var res = parser.Parse(row);
            switch (res.Outcome)
            {
                case RowOutcome.Room: parsed.Add(res.Room!); break;
                case RowOutcome.Blank: blank++; break;
                case RowOutcome.Divider: divider++; break;
                case RowOutcome.Invalid: invalid++; break;
                case RowOutcome.DepartmentHeader:
                    deptRows++;
                    if (!string.IsNullOrEmpty(res.DepartmentName)) names[res.DepartmentId!] = res.DepartmentName!;
                    break;
            }
        }

        // ---- pass 2 -----------------------------------------------------------------------------
        var rel = RelationshipChecks.Run(parsed, table.Sheet, map, diags);
        invalid += rel.DuplicateRowsDropped;

        var rows = ResolveColors(rel.Rows, table, map, diags);
        var types = rows.Select(r => r.Type).OrderBy(t => t.Id, ProgramIds.Comparer).ToList();
        var byId = types.ToDictionary(t => t.Id, StringComparer.Ordinal);

        // ---- instances: Qty explodes every attribute; area stays per room ----------------------
        var instances = types
            .Where(t => !t.Absorbed)
            .SelectMany(t => Enumerable.Range(0, t.Qty).Select(k => RoomInstance.Create(t, k)))
            .ToList();

        // ---- edges: instance k of A → instance min(k, Nb−1) of B --------------------------------
        var edges = new List<Edge>();
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolvedTypes = 0;
        var edgesSkipped = 0;
        foreach (var a in types)
        {
            if (a.Absorbed || a.ConnPrimary is null) continue;
            if (rel.UnwirableSources.Contains(a.Id))
            {
                unresolvedTypes++;
                edgesSkipped += a.Qty;
                continue;
            }
            var b = byId[a.ConnPrimary];
            next[a.Id] = b.Id;
            var kind = a.SharedAccess ? EdgeKind.SharedWall : EdgeKind.Adjacent;
            for (var k = 0; k < a.Qty; k++)
                edges.Add(new Edge(RoomInstance.MakeKey(a.Id, k), RoomInstance.MakeKey(b.Id, Math.Min(k, b.Qty - 1)), kind));
        }
        edges.Sort((x, y) =>
        {
            var c = ProgramIds.KeyComparer.Compare(x.FromKey, y.FromKey);
            return c != 0 ? c : ProgramIds.KeyComparer.Compare(x.ToKey, y.ToKey);
        });
        RelationshipChecks.ReportCycles(next, rows.ToDictionary(r => r.Type.Id, r => r.Row), table.Sheet, map, diags);

        // ---- departments, summary, hash --------------------------------------------------------
        var departments = types
            .Select(t => t.DepartmentId).Distinct()
            .OrderBy(d => d, ProgramIds.Comparer)
            .Select(d => new Department(d, names.TryGetValue(d, out var n) ? n : null))
            .ToList();

        var floors = instances
            .GroupBy(i => byId[i.TypeId].FloorPref)
            .OrderBy(g => g.Key.HasValue).ThenBy(g => g.Key)
            .Select(g => new FloorPreferenceSummary(g.Key, g.Count(), g.Sum(i => byId[i.TypeId].NetSf)))
            .ToList();

        var summary = new ImportSummary(
            RowsRead: table.Rows.Count,
            RoomTypes: types.Count,
            AbsorbedRoomTypes: types.Count(t => t.Absorbed),
            Instances: instances.Count,
            Edges: edges.Count,
            SharedWallEdges: edges.Count(e => e.Kind == EdgeKind.SharedWall),
            DividerRowsSkipped: divider,
            DepartmentHeaderRows: deptRows,
            BlankRowsSkipped: blank,
            InvalidRowsSkipped: invalid,
            UnresolvedRoomTypes: unresolvedTypes,
            EdgesSkipped: edgesSkipped,
            TotalNetSf: instances.Sum(i => byId[i.TypeId].NetSf),
            FloorPreferences: floors);

        var sortedDiags = Sorted(diags);
        var program = new BuildingProgram(
            BuildingProgram.CurrentSchemaVersion,
            Units.FeetSquareFeet,
            source,
            departments,
            types,
            instances,
            edges,
            sortedDiags,
            summary,
            ProgramHash: null);
        return new ImportResult(ProgramHasher.WithHash(program), sortedDiags);
    }

    /// <summary>
    /// Fills department-default colors and reports W007/W008. On the test workbook every Color RGB
    /// cell caches "255, 255, 255" (stale VBA), so one sheet-level W008 replaces 72 identical ones.
    /// </summary>
    private static List<ParsedRow> ResolveColors(IReadOnlyList<ParsedRow> rows, SourceTable table, ColumnMap map, List<Diagnostic> diags)
    {
        var deptColor = new Dictionary<string, RgbColor>(StringComparer.Ordinal);
        foreach (var r in rows.Where(r => r.ColorSource != ColorSource.DepartmentDefault).OrderBy(r => r.Type.Id, ProgramIds.Comparer))
            deptColor.TryAdd(r.Type.DepartmentId, r.Type.Color);

        var result = new List<ParsedRow>(rows.Count);
        foreach (var r in rows)
        {
            var t = r.Type;
            switch (r.ColorSource)
            {
                case ColorSource.RgbColumn when table.SupportsFill:
                    diags.Add(new Diagnostic(DiagnosticCodes.NoFillColor, Severity.Warning,
                        $"Row {r.Row}: {t.Id} {t.Name} has no fill in the Color column; using Color RGB ({t.Color}).",
                        table.Sheet, r.Row, map.Letter(ProgramColumn.Color), t.Id));
                    break;
                case ColorSource.DepartmentDefault:
                    var c = deptColor.TryGetValue(t.DepartmentId, out var dc) ? dc : RgbColor.MidGrey;
                    t = t with { Color = c };
                    diags.Add(new Diagnostic(DiagnosticCodes.NoFillColor, Severity.Warning,
                        $"Row {r.Row}: {t.Id} {t.Name} has no color; using department {t.DepartmentId}'s color ({c}).",
                        table.Sheet, r.Row, map.Letter(map.Has(ProgramColumn.Color) ? ProgramColumn.Color : ProgramColumn.ColorRgb), t.Id));
                    break;
            }
            result.Add(r with { Type = t });
        }

        var disagree = rows.Where(r => r.ColorSource == ColorSource.Fill && r.RgbColumnValue is { } v && v != r.Type.Color).ToList();
        var withFill = rows.Count(r => r.ColorSource == ColorSource.Fill && r.RgbColumnValue is not null);
        var col = map.Letter(ProgramColumn.ColorRgb);
        if (disagree.Count > 1 && disagree.Count == withFill && disagree.Select(r => r.RgbColumnValue).Distinct().Count() == 1)
        {
            diags.Add(new Diagnostic(DiagnosticCodes.RgbDisagreesWithFill, Severity.Warning,
                $"Color RGB (column {col}) reads '{disagree[0].RgbColumnValue}' on all {disagree.Count} rows and does not match the Color fills. It looks stale; colors were taken from the Color column fill.",
                table.Sheet, null, col));
        }
        else
        {
            foreach (var r in disagree)
                diags.Add(new Diagnostic(DiagnosticCodes.RgbDisagreesWithFill, Severity.Warning,
                    $"Row {r.Row}: {r.Type.Id} {r.Type.Name}: Color RGB says {r.RgbColumnValue} but the Color fill is {r.Type.Color}. Using the fill.",
                    table.Sheet, r.Row, col, r.Type.Id));
        }
        return result;
    }

    /// <summary>Deterministic order: sheet, row (sheet-level first), column, code, message.</summary>
    public static IReadOnlyList<Diagnostic> Sorted(IEnumerable<Diagnostic> diags) => diags
        .OrderBy(d => d.Sheet ?? "", StringComparer.Ordinal)
        .ThenBy(d => d.Row ?? 0)
        .ThenBy(d => d.Column?.Length ?? 0).ThenBy(d => d.Column ?? "", StringComparer.Ordinal)
        .ThenBy(d => d.Code, StringComparer.Ordinal)
        .ThenBy(d => d.TypeId ?? "", StringComparer.Ordinal)
        .ThenBy(d => d.Message, StringComparer.Ordinal)
        .ToList();
}
