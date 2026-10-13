using System.Security.Cryptography;
using System.Text;
using Psm.Data.Model;
using Psm.Data.Validation;

namespace Psm.Data.Csv;

/// <summary>
/// Reads a program table from CSV (RFC 4180: comma-separated, double-quoted fields may contain
/// commas, quotes as "" and line breaks). Headers are the same as the workbook's ProgramInfo sheet;
/// see docs/input-template.md. Numbers use '.' as the decimal separator regardless of culture.
/// CSV has no cell fills, so color comes from the "Color RGB" column ("168, 174, 73").
/// Ids are text, so "7.10" stays "7.10".
/// </summary>
public sealed class CsvProgramSource(string path) : IProgramSource
{
    public ImportResult Load()
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        var name = Path.GetFileName(path);
        return Parse(text, name, sha);
    }

    /// <summary>Parses CSV text. Exposed for tests and for callers that already hold the text.</summary>
    public static ImportResult Parse(string csv, string? fileName = null, string? sha256 = null)
    {
        var records = ReadRecords(csv);
        var sheet = fileName ?? "csv";
        var source = new ProgramSource("csv", fileName, null, sha256, null);

        // Header row: the first record that names both Dept No and Program Element.
        var headerIndex = records.FindIndex(r =>
            r.Any(h => ColumnMap.TryMatch(h, out var c) && c == ProgramColumn.DeptNo) &&
            r.Any(h => ColumnMap.TryMatch(h, out var c) && c == ProgramColumn.Name));
        if (headerIndex < 0)
        {
            var d = new Diagnostic(DiagnosticCodes.MissingColumn, Severity.Error,
                "No header row with 'Dept No' and 'Program Element' was found.", sheet);
            return new ImportResult(null, [d]);
        }

        var headers = records[headerIndex];
        var rows = new List<SourceRow>();
        for (var i = headerIndex + 1; i < records.Count; i++)
            rows.Add(new SourceRow(i + 1, records[i].Select(SourceCell.FromText).ToList()));

        var table = new SourceTable(sheet, headerIndex + 1, headers, rows, SupportsFill: false);
        return ProgramAssembler.Assemble(table, source);
    }

    /// <summary>RFC 4180 records. Row numbers in diagnostics are record numbers (1-based).</summary>
    private static List<List<string>> ReadRecords(string csv)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRecord()
        {
            EndField();
            records.Add(record);
            record = new List<string>();
        }

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"' when !fieldStarted && field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    if (i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                    EndRecord();
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(ch);
                    fieldStarted = true;
                    break;
            }
        }
        if (field.Length > 0 || record.Count > 0 || fieldStarted) EndRecord();
        return records;
    }
}
