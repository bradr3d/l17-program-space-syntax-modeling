namespace Psm.Data.Model;

/// <summary>
/// One row of the program table: a kind of room and how many of it the building needs.
/// Areas are per single room (not multiplied by <see cref="Qty"/>), in the program's units.
/// </summary>
/// <param name="Id">Dept No as text, e.g. "7.10" or "1.1.2".</param>
/// <param name="ParentId">Id minus its last segment ("1.1.2" → "1.1"). Null for "x.y" ids, whose parent is the department.</param>
/// <param name="DepartmentId">Integer before the first dot ("7.10" → "7").</param>
/// <param name="ConnPrimary">Id of the room this room pulls toward (Conn. Primary), or null.</param>
/// <param name="TotalSf">Total Program SF as written in the sheet; kept for the Qty × Net SF cross-check (W001).</param>
/// <param name="FloorPref">Preferred floor; null = no preference.</param>
/// <param name="SharedAccess">Opens directly onto its <see cref="ConnPrimary"/> room through a shared wall (no corridor door).</param>
/// <param name="Aspect">Aspect Ratio text as written ("1 to 1"); null when blank or "None".</param>
/// <param name="Absorbed">Qty 0: the space is absorbed into other rooms. Kept for reference; it has no instances.</param>
public sealed record RoomType(
    string Id,
    string? ParentId,
    string DepartmentId,
    string Name,
    int Qty,
    double NetSf,
    double? TotalSf,
    int? Stations,
    string? ConnPrimary,
    int? FloorPref,
    RgbColor Color,
    bool SharedAccess,
    double? MinWidthFt,
    string? Aspect,
    bool Absorbed,
    string? Notes,
    CellRef? Source);
