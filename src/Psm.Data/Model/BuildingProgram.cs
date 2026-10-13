namespace Psm.Data.Model;

/// <summary>Units used by every length and area in the program.</summary>
public sealed record Units(string Length, string Area)
{
    public static readonly Units FeetSquareFeet = new("ft", "sf");
}

/// <summary>Project metadata from the workbook header block (ProjectSheet A1:B5).</summary>
public sealed record ProjectInfo(
    string? Name,
    string? Client,
    string? Date,
    string? Revision,
    string? Number);

/// <summary>
/// Where the program was read from. Excluded from <see cref="BuildingProgram.ProgramHash"/>.
/// <see cref="FileName"/> is the file name only, so the JSON does not change between machines.
/// </summary>
public sealed record ProgramSource(
    string Kind,
    string? FileName,
    string? Sheet,
    string? FileSha256,
    ProjectInfo? Project);

/// <summary>A department. Names come from ProjectSheet rows whose Dept No is a whole number.</summary>
public sealed record Department(string Id, string? Name);

public sealed record FloorPreferenceSummary(int? Floor, int Rooms, double NetSf);

/// <summary>
/// Counts from the import. Every row or item that a step drops is counted here.
/// </summary>
public sealed record ImportSummary(
    int RowsRead,
    int RoomTypes,
    int AbsorbedRoomTypes,
    int Instances,
    int Edges,
    int SharedWallEdges,
    int DividerRowsSkipped,
    int DepartmentHeaderRows,
    int BlankRowsSkipped,
    int InvalidRowsSkipped,
    int UnresolvedRoomTypes,
    int EdgesSkipped,
    double TotalNetSf,
    IReadOnlyList<FloorPreferenceSummary> FloorPreferences);

/// <summary>The canonical, source-independent room program.</summary>
public sealed record BuildingProgram(
    int SchemaVersion,
    Units Units,
    ProgramSource? Source,
    IReadOnlyList<Department> Departments,
    IReadOnlyList<RoomType> RoomTypes,
    IReadOnlyList<RoomInstance> Instances,
    IReadOnlyList<Edge> Edges,
    IReadOnlyList<Diagnostic> Diagnostics,
    ImportSummary? Summary,
    string? ProgramHash)
{
    public const int CurrentSchemaVersion = 1;

    public RoomType? FindType(string typeId)
    {
        foreach (var t in RoomTypes)
            if (t.Id == typeId) return t;
        return null;
    }
}

/// <summary>
/// Result of reading a program. <see cref="Program"/> is null only when nothing usable could be read
/// (e.g. required columns missing). When <see cref="Ok"/> is false the program is a best effort
/// built from the rows that passed validation.
/// </summary>
public sealed record ImportResult(BuildingProgram? Program, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Ok => Program is not null && !Diagnostics.Any(d => d.Severity == Severity.Error);
}
