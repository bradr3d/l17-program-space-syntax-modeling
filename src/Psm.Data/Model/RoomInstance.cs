using System.Globalization;

namespace Psm.Data.Model;

/// <summary>
/// One physical room. <see cref="Key"/> is "{TypeId}#{Index}" with a 0-based index ("1.1#0"),
/// matching the prototype, the handoff docs and the prototype screenshots.
/// </summary>
public sealed record RoomInstance(string Key, string TypeId, int Index, string Label)
{
    public static string MakeKey(string typeId, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{typeId}#{index}");

    /// <summary>"{TypeId} {Name} ({Index+1}/{Qty})", e.g. "1.1 Classroom - K (1/4)".</summary>
    public static string MakeLabel(RoomType type, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{type.Id} {type.Name} ({index + 1}/{type.Qty})");

    public static RoomInstance Create(RoomType type, int index) =>
        new(MakeKey(type.Id, index), type.Id, index, MakeLabel(type, index));
}
