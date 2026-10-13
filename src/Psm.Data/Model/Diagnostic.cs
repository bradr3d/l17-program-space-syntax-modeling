namespace Psm.Data.Model;

public enum Severity
{
    Error,
    Warning,
    Info,
}

/// <summary>
/// One finding about the input. Validation never throws on bad input; it returns these.
/// <see cref="Row"/> is the 1-based sheet row and <see cref="Column"/> the column letter, when known.
/// </summary>
public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    string? Sheet = null,
    int? Row = null,
    string? Column = null,
    string? TypeId = null);

/// <summary>Diagnostic codes. The table in CLAUDE.md is the source of truth for their meaning.</summary>
public static class DiagnosticCodes
{
    public const string DuplicateId = "E001";
    public const string MissingColumn = "E002";
    public const string UnknownConnection = "E003";
    public const string MissingParent = "E004";
    public const string BadValue = "E005";
    public const string InvalidId = "E006";
    public const string NoCachedValue = "E007";

    public const string TotalMismatch = "W001";
    public const string Absorbed = "W002";
    public const string ConnectionToAbsorbed = "W003";
    public const string IdStoredAsNumber = "W004";
    public const string ChildQtyExceedsParent = "W005";
    public const string AreaBelowMinWidth = "W006";
    public const string NoFillColor = "W007";
    public const string RgbDisagreesWithFill = "W008";

    public const string AdjacencyCycle = "I001";
    public const string UnknownColumn = "I002";
    public const string RepeatedDepartmentHeader = "I003";
}
