using System.Globalization;

namespace Psm.Data.Model;

/// <summary>An opaque 8-bit RGB color.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor White = new(255, 255, 255);
    public static readonly RgbColor MidGrey = new(128, 128, 128);

    /// <summary>
    /// Parses "R, G, B" (the format written by the workbook's <c>findcolor</c> VBA function).
    /// Returns false for anything else, including out-of-range components.
    /// </summary>
    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',');
        if (parts.Length != 3) return false;
        var c = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                || v < 0 || v > 255)
                return false;
            c[i] = (byte)v;
        }
        color = new RgbColor(c[0], c[1], c[2]);
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{R}, {G}, {B}");
}
