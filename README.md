# Week 1 Build

This folder is the current C#/.NET build and testing root.

- `Rhino2DGeometry/`: Rhino 8 plug-in, build instructions, and Rhino smoke test. See [the project guide](Rhino2DGeometry/README.md).
- `Rhino2DGeometry.Tests/`: standalone engine tests.
- `ExcelToBubbleDiagramCommand.cs`: active C# source linked by the plug-in project; keep it here.
- `Program_ExcelToGrasshopper_Test.xlsm` and `.xlsx`: shared workbook inputs used for current testing. Their historical names are retained.
- `grasshopper_build/`: archived Grasshopper definitions, Python components, handoff notes, screenshots, parsing tools, and legacy test results.

Run from this root in PowerShell:

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" build .\Rhino2DGeometry\Rhino2DGeometry.csproj -c Release
& "$env:ProgramFiles\dotnet\dotnet.exe" run --project .\Rhino2DGeometry.Tests\Rhino2DGeometry.Tests.csproj -c Release
```

The current plug-in still uses Rhino's installed KangarooSolver library, supplied with Grasshopper. Keep that runtime dependency installed.
