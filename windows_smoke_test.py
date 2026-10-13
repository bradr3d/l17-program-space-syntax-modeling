#! python3
# r: openpyxl
"""Run using RhinoCode to verify the Windows dependencies and workbook reader."""
from pathlib import Path
import json
import sys
import Rhino
import Grasshopper
import System
import openpyxl

root = Path(__file__).resolve().parent
report = {
    "python": sys.version,
    "executable": sys.executable,
    "openpyxl": openpyxl.__version__,
    "rhino": str(Rhino.RhinoApp.Version),
    "grasshopper_import": True,
}
for path in sorted((root / "python_files").glob("*.py")):
    compile(path.read_text(encoding="utf-8-sig"), str(path), "exec")
report["component_syntax"] = "All six scripts passed"
inputs = {
    "xlsx_path": str(root / "Program_ExcelToGrasshopper_Test.xlsm"),
    "sheet_name": "Sheet2", "run": True,
}
wb = openpyxl.load_workbook(inputs["xlsx_path"], data_only=True, read_only=True)
report["worksheets"] = wb.sheetnames
for ws in wb.worksheets:
    for row_number, row in enumerate(ws.iter_rows(max_row=30, values_only=True), 1):
        headers = [str(value).strip().lower() for value in row if value is not None]
        if "dept no" in headers and "program element" in headers and "flr" in headers and any("conn" in h for h in headers):
            inputs["sheet_name"] = ws.title
            inputs["header_row"] = row_number
            break
    if "header_row" in inputs:
        break
wb.close()
report["reader_sheet"] = inputs["sheet_name"]
report["reader_header_row"] = inputs.get("header_row", 1)
reader = root / "python_files" / "Comp01_Reader.py"
exec(compile(reader.read_text(encoding="utf-8-sig"), str(reader), "exec"), inputs)
report["reader_info"] = inputs["info"]
report["room_count"] = len(inputs["uid"])
report["unique_uids"] = len(set(inputs["uid"]))
report["total_area_sf"] = sum(inputs["area"])
report["reader_passed"] = bool(inputs["uid"]) and len(set(inputs["uid"])) == len(inputs["uid"])
report["workbook_headers"] = {}
for workbook_path in root.glob("Program_*.xls*"):
    book = openpyxl.load_workbook(str(workbook_path), data_only=True, read_only=True)
    sheets = {}
    for sheet in book.worksheets:
        for index, row in enumerate(sheet.iter_rows(max_row=30, values_only=True), 1):
            if any(str(v).strip().lower() == "dept no" for v in row):
                sheets[sheet.title] = {"row": index, "headers": [str(v) if v is not None else "" for v in row]}
                break
    report["workbook_headers"][workbook_path.name] = sheets
    program_rows = list(book["ProgramInfo"].iter_rows(min_row=2, values_only=True))
    report.setdefault("program_cache_counts", {})[workbook_path.name] = {
        "rows": len(program_rows),
        "nonempty_dept": sum(row[0] is not None for row in program_rows),
        "nonempty_qty": sum(row[2] is not None for row in program_rows),
        "first_three_rows": [list(row[:4]) for row in program_rows[:3]],
    }
    book.close()
    trial = {"xlsx_path": str(workbook_path), "sheet_name": "ProgramInfo", "header_row": 1, "run": True}
    exec(compile(reader.read_text(encoding="utf-8-sig"), str(reader), "exec"), trial)
    report.setdefault("workbook_reader_results", {})[workbook_path.name] = {"info": trial["info"], "rooms": len(trial["uid"])}
    report["workbook_reader_results"][workbook_path.name]["white_rows"] = sum(trial["_is_white"](row[11]) for row in program_rows)
    report["workbook_reader_results"][workbook_path.name]["positive_qty_rows"] = sum((trial["_num"](row[2], 0) or 0) > 0 for row in program_rows)
output = root / "windows_smoke_test_results.json"
output.write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
if not report["reader_passed"]:
    raise RuntimeError("Spreadsheet reader failed; inspect the results JSON")
