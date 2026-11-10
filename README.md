# Week 1 Build

This folder is the current C#/.NET build and testing root.

`Generate2DFloorplan` now also creates 14-foot 3D room masses, leaves corridors and stairs flat, and exports `floorplan.svg` here. See [rebuild and Rhino command instructions](Rhino2DGeometry/README.md#rebuild-and-install-after-changes).

- `Rhino2DGeometry/`: Rhino 8 plug-in, build instructions, and Rhino smoke test. See [the project guide](Rhino2DGeometry/README.md).
- `BubbleSolver/`: independent .NET 8 solver component, referenced by the plug-in through a build-owned adapter. See [the integration guide](Rhino2DGeometry/README.md#bubble-solver-component).
- `Rhino2DGeometry.Tests/`: standalone engine tests.
- `ExcelToBubbleDiagramCommand.cs`: active C# source linked by the plug-in project; keep it here.
- `Program_ExcelToGrasshopper_Test.xlsm` and `.xlsx`: shared workbook inputs used for current testing. Their historical names are retained.
- `grasshopper_build/`: archived Grasshopper definitions, Python components, handoff notes, screenshots, parsing tools, and legacy test results.

Run from this root in PowerShell:

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" build .\Rhino2DGeometry\Rhino2DGeometry.csproj -c Release
& "$env:ProgramFiles\dotnet\dotnet.exe" run --project .\Rhino2DGeometry.Tests\Rhino2DGeometry.Tests.csproj -c Release
```

The plug-in and tests target .NET 8. Automatic circular diagrams use the separate BubbleSolver component. Squares and selected-boundary diagrams use Rhino's installed KangarooSolver library, supplied with Grasshopper. Keep that runtime dependency installed.
