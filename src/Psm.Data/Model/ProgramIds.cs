using System.Text.RegularExpressions;

namespace Psm.Data.Model;

/// <summary>Helpers for Dept No ids like "1.1", "7.10", "1.1.2".</summary>
public static partial class ProgramIds
{
    [GeneratedRegex(@"^\d+(\.\d+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>A room id has at least two dot-separated numeric segments. "7" (a department) is not one.</summary>
    public static bool IsValid(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>"7.10" → "7".</summary>
    public static string DepartmentOf(string id) => id[..id.IndexOf('.')];

    /// <summary>"1.1.2" → "1.1"; "1.1" → null (its parent is the department).</summary>
    public static string? ParentOf(string id)
    {
        var last = id.LastIndexOf('.');
        var parent = id[..last];
        return parent.Contains('.') ? parent : null;
    }

    /// <summary>
    /// Orders ids segment by segment as numbers: 1.1 &lt; 1.1.1 &lt; 1.2 &lt; 1.10 &lt; 2.1.
    /// Falls back to ordinal comparison for anything that is not a valid id, so the order is total.
    /// </summary>
    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    /// <summary>Orders instance keys "{id}#{k}" by id, then by k as a number.</summary>
    public static readonly IComparer<string> KeyComparer = Comparer<string>.Create(CompareKeys);

    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var sa = a.Split('.');
        var sb = b.Split('.');
        for (var i = 0; i < Math.Min(sa.Length, sb.Length); i++)
        {
            var c = CompareNumericText(sa[i], sb[i]);
            if (c != 0) return c;
        }
        var len = sa.Length.CompareTo(sb.Length);
        return len != 0 ? len : string.CompareOrdinal(a, b);
    }

    private static int CompareKeys(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var ha = a.LastIndexOf('#');
        var hb = b.LastIndexOf('#');
        if (ha < 0 || hb < 0) return Compare(a, b);
        var c = Compare(a[..ha], b[..hb]);
        return c != 0 ? c : CompareNumericText(a[(ha + 1)..], b[(hb + 1)..]);
    }

    /// <summary>Compares digit strings by numeric value without parsing (no overflow); others ordinally.</summary>
    private static int CompareNumericText(string x, string y)
    {
        if (x.Length > 0 && y.Length > 0 && x.All(char.IsAsciiDigit) && y.All(char.IsAsciiDigit))
        {
            var tx = x.TrimStart('0');
            var ty = y.TrimStart('0');
            if (tx.Length != ty.Length) return tx.Length.CompareTo(ty.Length);
            var c = string.CompareOrdinal(tx, ty);
            return c != 0 ? c : x.Length.CompareTo(y.Length);
        }
        return string.CompareOrdinal(x, y);
    }
}
