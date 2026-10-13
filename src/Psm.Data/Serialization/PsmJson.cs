using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Psm.Data.Model;

namespace Psm.Data.Serialization;

/// <summary>
/// The one place JSON settings are defined. Every DAL type is written with these options so
/// files are byte-identical for the same input on every machine and culture.
/// </summary>
public static class PsmJson
{
    /// <summary>Indented, for files people read and diff.</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    /// <summary>Compact, for hashing and user-text strings.</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = indented,
            // Room names contain '&' and '\''; keep them readable. These files are not embedded in HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.Strict,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>Indented JSON with LF line endings on every OS.</summary>
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Indented).Replace("\r\n", "\n") + "\n";

    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Indented);
}

/// <summary>Stable content hash of a program, independent of where and when it was imported.</summary>
public static class ProgramHasher
{
    /// <summary>Fields left out of the hash: provenance and import reports, plus the hash itself.</summary>
    private static readonly string[] Excluded = ["source", "diagnostics", "summary", "programHash"];

    /// <summary>"sha256:" + lowercase hex of the compact JSON without the excluded fields.</summary>
    public static string Compute(BuildingProgram program)
    {
        var node = JsonSerializer.SerializeToNode(program, PsmJson.Compact)!.AsObject();
        foreach (var name in Excluded) node.Remove(name);
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(PsmJson.Compact));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static BuildingProgram WithHash(BuildingProgram program) =>
        program with { ProgramHash = Compute(program) };
}
