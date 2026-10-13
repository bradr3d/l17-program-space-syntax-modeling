using System.Globalization;
using System.Text;

namespace Psm.Data.Validation;

public enum IdFormatOutcome
{
    /// <summary>The number reads the same with or without its format (e.g. 1.1 in General).</summary>
    Plain,

    /// <summary>The number format changed the text, and that recovered the intended id (7.1 with "0.00" → "7.10").</summary>
    Recovered,

    /// <summary>The format hides digits the cell really holds (7.123 shown as "7.12"), or the format can't be read.</summary>
    Doubtful,
}

/// <summary>
/// Turns a numeric Dept No / Conn. Primary cell into id text the way Excel displays it.
/// Motivation: ProgramInfo!A61 is "7.10" stored as the number 7.1 with format "0.00"; reading
/// the bare double gives "7.1" and collides with General Admin Reception (row 47).
/// </summary>
public static class IdText
{
    public static string FromNumber(double value, string? numberFormat, out IdFormatOutcome outcome)
    {
        var plain = value.ToString("R", CultureInfo.InvariantCulture);
        var spec = ReadDecimals(numberFormat);
        if (spec is null)
        {
            // General (or no format): Excel shows the shortest form, which is what we use.
            outcome = IsGeneral(numberFormat) ? IdFormatOutcome.Plain : IdFormatOutcome.Doubtful;
            return plain;
        }

        var (min, max) = spec.Value;
        var text = value.ToString("F" + max.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        // Optional '#' digits: Excel drops their trailing zeros down to the '0' minimum.
        if (max > min && text.Contains('.'))
        {
            var dot = text.IndexOf('.');
            var keep = text.Length;
            while (keep > dot + 1 + min && text[keep - 1] == '0') keep--;
            text = text[..keep];
            if (text.EndsWith('.')) text = text[..^1];
        }

        var reparsed = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (Math.Abs(reparsed - value) > 1e-9 * Math.Max(1, Math.Abs(value)))
            outcome = IdFormatOutcome.Doubtful;
        else
            outcome = text == plain ? IdFormatOutcome.Plain : IdFormatOutcome.Recovered;
        return text;
    }

    private static bool IsGeneral(string? fmt) =>
        string.IsNullOrWhiteSpace(fmt) || fmt.Trim().Equals("General", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads (minimum, maximum) decimal places from the first section of a number format, e.g.
    /// "0.00" → (2,2), "0.0#" → (1,2), "#,##0" → (0,0). Returns null for General, and for formats
    /// that aren't plain decimals (percent, scientific, fractions, dates), which can't hold an id.
    /// </summary>
    public static (int Min, int Max)? ReadDecimals(string? numberFormat)
    {
        if (IsGeneral(numberFormat)) return null;
        var section = StripLiterals(numberFormat!.Split(';')[0]);
        if (section.IndexOfAny(['%', 'E', 'e', '/', 'y', 'm', 'd', 'h', 's', '@']) >= 0) return null;
        if (!section.Contains('0') && !section.Contains('#')) return null;
        var dot = section.IndexOf('.');
        if (dot < 0) return (0, 0);
        int min = 0, max = 0;
        for (var i = dot + 1; i < section.Length; i++)
        {
            var c = section[i];
            if (c == '0') { min = max + 1; max++; }
            else if (c is '#' or '?') max++;
            else break;
        }
        return (min, max);
    }

    /// <summary>Removes "quoted text", \-escapes, _x spacers, *x fills and [colour/condition] blocks.</summary>
    private static string StripLiterals(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"':
                    i = s.IndexOf('"', i + 1);
                    if (i < 0) return sb.ToString();
                    break;
                case '[':
                    i = s.IndexOf(']', i + 1);
                    if (i < 0) return sb.ToString();
                    break;
                case '\\' or '_' or '*':
                    i++;
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
