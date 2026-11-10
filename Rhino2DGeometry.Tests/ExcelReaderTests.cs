using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using ExcelToBubbleDiagram;

internal static class ExcelReaderTests
{
    public static string Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BubbleReaderTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "program.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("ProgramInfo");
                var headers = new[] { "Dept No", "Program Element", "Room Qty/ Units", "Net SF/ Units", "Conn. Primary", "Color RGB", "Flr" };
                for (var column = 0; column < headers.Length; column++)
                    sheet.Cell(1, column + 1).Value = headers[column];
                SetRoom(sheet, 2, "1.1", "First room", 2, 100);
                for (var column = 1; column <= 7; column++)
                    sheet.Cell(3, column).FormulaA1 = "0";
                SetRoom(sheet, 4, "2.1", "Next department", 1, 200);
                sheet.Cell(4, 5).Value = "1.1";
                SetRoom(sheet, 5, "2.2", "Omitted room", 0, 0);
                sheet.Cell(5, 4).Clear();
                // A completely empty row must also remain harmless.
                SetRoom(sheet, 7, "3.1", "Last department", 1, 300);
                workbook.SaveAs(path);
            }

            var data = BubbleDiagramExcelReader.Load(path, "ProgramInfo", 1);
            Check(data.Nodes.Count == 4, "Separator and omitted room must not create nodes.");
            Check(data.TotalSquareFeet == 700, "Area totals must exclude omitted rows.");
            Check(data.Nodes.Any(node => node.Source.ExcelRow == 7), "Import must continue past department breaks.");
            Check(data.Connections.Count == 1, "Connections across department breaks must resolve.");
            Check(data.Warnings.Count == 1 && data.Warnings[0].Contains("Row 5:"), "Named zero-quantity rooms must be reported; separators stay quiet.");

            ExpectInvalid(path, -1, 100, "Room Qty/ Units");
            ExpectInvalid(path, null, 100, "Room Qty/ Units");
            ExpectInvalid(path, "invalid", 100, "Room Qty/ Units");
            ExpectInvalid(path, 1, 0, "Net SF/ Units");
            ExpectInvalid(path, 1, -100, "Net SF/ Units");
            ExpectInvalid(path, 1, null, "Net SF/ Units");

            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Program_ExcelToGrasshopper_Test.xlsx")))
                root = root.Parent;
            Check(root != null, "Could not locate root workbook regression fixtures.");
            foreach (var extension in new[] { ".xlsx", ".xlsm" })
            {
                var actual = BubbleDiagramExcelReader.Load(
                    Path.Combine(root.FullName, "Program_ExcelToGrasshopper_Test" + extension), "ProgramInfo", 1);
                Check(actual.Nodes.Count == 151, extension + ": unexpected room count.");
                Check(actual.TotalSquareFeet == 48092, extension + ": unexpected area total.");
                Check(actual.Nodes.Any(node => node.Source.ExcelRow == 18), extension + ": lost department after row 17.");
                Check(actual.Nodes.Any(node => node.Source.ExcelRow == 81), extension + ": lost final department.");
                Check(actual.Nodes.All(node => node.Source.Quantity > 0), extension + ": zero-quantity room was included.");
                Check(actual.Warnings.Count(warning => warning.Contains("because 'Room Qty/ Units' is 0")) == 4,
                    extension + ": expected four omitted-room warnings.");
            }
            return "Excel reader tests passed (department breaks, omitted rooms, validation, and both root workbooks).";
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void SetRoom(IXLWorksheet sheet, int row, string department, string name, int quantity, double area)
    {
        sheet.Cell(row, 1).Value = department;
        sheet.Cell(row, 2).Value = name;
        sheet.Cell(row, 3).Value = quantity;
        sheet.Cell(row, 4).Value = area;
        sheet.Cell(row, 6).Value = "255, 255, 255";
        sheet.Cell(row, 7).Value = "1";
    }

    private static void ExpectInvalid(string path, object quantity, object area, string field)
    {
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet("ProgramInfo");
            sheet.Cell(2, 3).Value = XLCellValue.FromObject(quantity);
            sheet.Cell(2, 4).Value = XLCellValue.FromObject(area);
            workbook.Save();
        }
        try
        {
            BubbleDiagramExcelReader.Load(path, "ProgramInfo", 1);
        }
        catch (InvalidDataException exception)
        {
            Check(exception.Message.Contains("Row 2:") && exception.Message.Contains(field), "Validation must identify the row and field.");
            return;
        }
        throw new Exception("Expected validation error for " + field);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
