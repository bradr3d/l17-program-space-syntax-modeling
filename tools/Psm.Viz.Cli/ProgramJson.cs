// Minimal reader for the DAL's program JSON (schema v1).
// Once dal-init is merged, swap this for Psm.Data's JsonProgramSource / ExcelProgramSource.

using System.Text.Json;
using BubbleSolver;
using Psm.Viz;

namespace Psm.Viz.Cli;

public sealed record RoomInfo(string Uid, string TypeId, string Name, string Label, double NetSf,
                              string Color, int Floor, string DepartmentId);

public sealed record ProgramEdge(string From, string To, bool SharedWall);

public sealed class LoadedProgram
{
    public LinearUnit Units { get; init; }
    public List<RoomInfo> Rooms { get; } = new();
    public List<ProgramEdge> Edges { get; } = new();
    public Dictionary<string, string> Departments { get; } = new();
    public string SourceName { get; init; } = "";

    public static LoadedProgram Read(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;

        string? lengthUnit = root.GetProperty("units").GetProperty("length").GetString();
        string sourceName = Path.GetFileName(path);

        if (root.TryGetProperty("source", out JsonElement src) && src.ValueKind == JsonValueKind.Object
            && src.TryGetProperty("fileName", out JsonElement fn) && fn.ValueKind == JsonValueKind.String)
        {
            sourceName = fn.GetString()!;
        }

        LoadedProgram p = new()
        {
            Units = lengthUnit == "m" ? LinearUnit.Meters : LinearUnit.Feet,
            SourceName = sourceName,
        };

        foreach (JsonElement d in root.GetProperty("departments").EnumerateArray())
        {
            p.Departments[d.GetProperty("id").GetString()!] = d.GetProperty("name").GetString() ?? "";
        }

        Dictionary<string, JsonElement> types = root.GetProperty("roomTypes").EnumerateArray()
                                                    .ToDictionary(t => t.GetProperty("id").GetString()!);

        foreach (JsonElement inst in root.GetProperty("instances").EnumerateArray())
        {
            JsonElement t = types[inst.GetProperty("typeId").GetString()!];
            JsonElement c = t.GetProperty("color");
            JsonElement flr = t.GetProperty("floorPref");

            p.Rooms.Add(new RoomInfo(
                Uid: inst.GetProperty("key").GetString()!,
                TypeId: t.GetProperty("id").GetString()!,
                Name: t.GetProperty("name").GetString()!,
                Label: inst.GetProperty("label").GetString()!,
                NetSf: t.GetProperty("netSf").GetDouble(),
                Color: Style.Rgb(c.GetProperty("r").GetByte(), c.GetProperty("g").GetByte(), c.GetProperty("b").GetByte()),
                Floor: flr.ValueKind == JsonValueKind.Number ? flr.GetInt32() : 1,   // prototype rule: no pref -> floor 1
                DepartmentId: t.GetProperty("departmentId").GetString()!));
        }

        foreach (JsonElement e in root.GetProperty("edges").EnumerateArray())
        {
            p.Edges.Add(new ProgramEdge(e.GetProperty("fromKey").GetString()!,
                                        e.GetProperty("toKey").GetString()!,
                                        e.GetProperty("kind").GetString() == "sharedWall"));
        }

        return p;
    }

    public List<RoomInput> ToSolverRooms() => Rooms.Select(r => new RoomInput
    {
        Uid = r.Uid,
        Area = r.NetSf,
        Floor = r.Floor,
        Label = r.Label,
        Dept = int.TryParse(r.DepartmentId, out int d) ? d : -1,
    }).ToList();

    public List<AdjacencyEdge> ToSolverEdges() => Edges.Select(e => new AdjacencyEdge(e.From, e.To)).ToList();
}