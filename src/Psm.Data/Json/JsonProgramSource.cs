using System.Text.Json;
using Psm.Data.Model;
using Psm.Data.Serialization;

namespace Psm.Data.Json;

/// <summary>
/// Reads canonical program JSON (as written by <see cref="PsmJson.Serialize{T}"/>). Lets teammates
/// work from a JSON fixture without Excel. The stored hash is checked and recomputed.
/// </summary>
public sealed class JsonProgramSource(string path) : IProgramSource
{
    public ImportResult Load()
    {
        string json;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
            json = reader.ReadToEnd();
        return Parse(json, Path.GetFileName(path));
    }

    public static ImportResult Parse(string json, string? fileName = null)
    {
        Diagnostic Fail(string msg) =>
            new(DiagnosticCodes.BadValue, Severity.Error, msg, fileName);

        int version;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var v) || !v.TryGetInt32(out version))
                return new ImportResult(null, [Fail("The JSON has no integer 'schemaVersion'; it is not a program file.")]);
        }
        catch (JsonException e)
        {
            return new ImportResult(null, [Fail($"The file is not valid JSON: {e.Message}")]);
        }

        if (version > BuildingProgram.CurrentSchemaVersion)
            return new ImportResult(null, [Fail($"The program file uses schema version {version}, newer than this plugin supports ({BuildingProgram.CurrentSchemaVersion}). Update the plugin.")]);

        BuildingProgram? program;
        try
        {
            program = ProgramMigrations.Deserialize(json, version);
        }
        catch (JsonException e)
        {
            return new ImportResult(null, [Fail($"The program JSON does not match schema version {version}: {e.Message}")]);
        }
        if (program is null)
            return new ImportResult(null, [Fail("The program JSON is empty.")]);

        var diags = new List<Diagnostic>(program.Diagnostics ?? []);
        var hash = ProgramHasher.Compute(program);
        if (program.ProgramHash is not null && program.ProgramHash != hash)
        {
            diags.Add(new Diagnostic(DiagnosticCodes.BadValue, Severity.Warning,
                "The stored programHash does not match the content; the file was edited by hand. The hash was recomputed.", fileName));
        }
        var sorted = ProgramAssembler.Sorted(diags);
        program = program with { ProgramHash = hash, Diagnostics = sorted };
        return new ImportResult(program, sorted);
    }
}
