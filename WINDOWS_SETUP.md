# Windows test setup

Verified on October 4, 2026.

## Installed and verified

- Rhino 8.35 is installed, including Grasshopper and RhinoCommon.
- Initialized Rhino's bundled CPython runtime at `C:\Users\Kevin\.rhinocode\py39-rh8`.
- Installed `openpyxl==3.1.5` and its dependency `et-xmlfile==2.0.0` into that runtime.
- Verified imports inside a running Rhino instance: Rhino, Grasshopper, System, openpyxl.
- All six component Python files compile successfully.

The six supplied components require no additional external Python packages. A separate Python installation, Node.js, Visual Studio, and Microsoft Excel are not required to read the existing workbook and run these components. Excel may be useful for editing/recalculating the workbook.

## Grasshopper test settings

1. Open Rhino and run `Grasshopper`.
2. Open the intended `.gh` definition. The folder contains both `ExcelToBubbleDiagram.gh` and `Space Syntax Modeling_0.3.gh`; their embedded scripts/wiring have not been verified against the loose Python sources.
3. Component 01 must use Python 3 and retain `# r: openpyxl` on line 2.
4. Set `xlsx_path` to `C:\Users\Kevin\Documents\Senior Design\Treadway Build\Program_ExcelToGrasshopper_Test.xlsm`.
5. Set `sheet_name` to `ProgramInfo`, `header_row` to `1`, and `run` to `True`. The handoff's `Sheet2` does not exist in either supplied workbook.
6. Use the input access/type settings documented at the top of each component script. Tree inputs require Tree access.

## Current blockers

The read-only reader smoke test fails for both workbook versions with `ERROR: no usable rows after cleaning.` All 81 ProgramInfo data rows have white `Color RGB` values. Component 01 explicitly skips white rows as spacers, including 68 rows with positive quantities. Correct the intended room colors or revise the reader's spacer rule before expecting geometry. The source workbooks and component scripts were left unchanged.

The handoff also describes a later revision than the supplied loose scripts: `floor_pref`, the floor-assignment inputs, and `lbl_in` are absent, and Comp04 reads/prints `split_mode` without implementing the documented switch. Installing packages cannot supply those missing changes.

Do not save the macro workbook through openpyxl: the handoff warns that this can discard cached array-formula values.

## Repeatable smoke test

Run `StartScriptServer` in Rhino, then use PowerShell:

```powershell
& 'C:\Program Files\Rhino 8\System\RhinoCode.exe' script 'C:\Users\Kevin\Documents\Senior Design\Treadway Build\windows_smoke_test.py'
```

The script imports the runtime dependencies, compiles the component sources, and reads both workbooks without saving them. It writes `windows_smoke_test_results.json` and raises an error if the reader returns no rooms. It does not validate final geometry or Grasshopper canvas wiring.

Official runtime documentation: https://developer.rhino3d.com/guides/scripting/advanced-langinit/
