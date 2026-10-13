using System.Text.Json;
using System.Text.Json.Nodes;
using Psm.Data.Model;

namespace Psm.Data.Serialization;

/// <summary>
/// Upgrades older program JSON to <see cref="BuildingProgram.CurrentSchemaVersion"/>.
/// To add version N+1: bump CurrentSchemaVersion, add a step N → N+1 to <see cref="Steps"/> that edits
/// the JSON tree, and keep a fixture of version N with a test that it still loads.
/// </summary>
public static class ProgramMigrations
{
    /// <summary>Step i upgrades version i+1 to version i+2. Empty while only version 1 exists.</summary>
    private static readonly Func<JsonObject, JsonObject>[] Steps = [];

    public static BuildingProgram? Deserialize(string json, int version)
    {
        if (version == BuildingProgram.CurrentSchemaVersion)
            return PsmJson.Deserialize<BuildingProgram>(json);
        if (version < 1)
            throw new JsonException($"Unknown schema version {version}.");

        var node = JsonNode.Parse(json)!.AsObject();
        for (var v = version; v < BuildingProgram.CurrentSchemaVersion; v++)
        {
            node = Steps[v - 1](node);
            node["schemaVersion"] = v + 1;
        }
        return node.Deserialize<BuildingProgram>(PsmJson.Indented);
    }
}
