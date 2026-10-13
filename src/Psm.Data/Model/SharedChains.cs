namespace Psm.Data.Model;

/// <summary>
/// Groups instances joined by <see cref="EdgeKind.SharedWall"/> edges (transitively) into "atomic
/// chains", e.g. 1.1#0 classroom + 1.1.1#0 restroom + 1.1.2#0 vestibule. A chain must stay together
/// on one floor so its direct doors can be placed. Pure data: this does not assign floors or place rooms.
/// </summary>
public static class SharedChains
{
    /// <summary>
    /// Every instance appears in exactly one chain (rooms with no shared walls form chains of one).
    /// Chains are sorted internally by key, and ordered by their first key.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Compute(BuildingProgram program)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var i in program.Instances) parent[i.Key] = i.Key;

        string Find(string x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        foreach (var e in program.Edges)
        {
            if (e.Kind != EdgeKind.SharedWall || !parent.ContainsKey(e.FromKey) || !parent.ContainsKey(e.ToKey)) continue;
            var a = Find(e.FromKey);
            var b = Find(e.ToKey);
            if (a == b) continue;
            // Smaller key becomes the root so the result never depends on edge order.
            if (ProgramIds.KeyComparer.Compare(a, b) < 0) parent[b] = a; else parent[a] = b;
        }

        return program.Instances
            .GroupBy(i => Find(i.Key))
            .Select(g => (IReadOnlyList<string>)g.Select(i => i.Key).OrderBy(k => k, ProgramIds.KeyComparer).ToList())
            .OrderBy(c => c[0], ProgramIds.KeyComparer)
            .ToList();
    }
}
