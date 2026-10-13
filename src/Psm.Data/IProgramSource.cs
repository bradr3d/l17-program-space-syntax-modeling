using Psm.Data.Model;

namespace Psm.Data;

/// <summary>
/// Anything that can produce a <see cref="BuildingProgram"/>: an Excel workbook, a CSV file or canonical JSON.
/// Bad content comes back as diagnostics in the result. Exceptions mean a real failure:
/// file missing or locked, or another I/O error.
/// </summary>
public interface IProgramSource
{
    ImportResult Load();
}
