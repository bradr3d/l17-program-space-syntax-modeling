# Rhino 2D Geometry

Rhino 8 plug-in (`net7.0-windows`) with two commands:

- `ExcelToBubbleDiagram` — reads a program workbook, sizes bubbles from net SF, and relaxes a constrained bubble diagram with Kangaroo2.
- `Generate2DFloorplan` — converts selected metadata-tagged bubble circles into 2D room walls, corridor ribbons, wall openings, door leaves, and swing arcs.

`ExcelToBubbleDiagram` ports the archived Grasshopper definition in `../grasshopper_build/ExcelToBubbleDiagram.ghx`. `Generate2DFloorplan` ports Components 03–06 of the Excel → Grasshopper pipeline. Bubble positions come from Rhino circles; room metadata comes from object user text. Shared Excel workbooks remain in the project root, one folder above this guide.

## Build and load

1. From this folder, build:

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" build -c Release
```

2. Run the out-of-Rhino engine tests:

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" run --project ..\Rhino2DGeometry.Tests\Rhino2DGeometry.Tests.csproj -c Release
```

Those tests cover metadata parsing, floor assignment, clustering, exact room area, shared-access packing, and corridor-width formulas. Geometry bake tests run from the `Test2DFloorplanEngine` command inside Rhino.

3. In Rhino 8, run `PluginManager`, choose **Install**, and select:
   `bin\Release\Rhino2DGeometry.rhp`.
4. Run `ExcelToBubbleDiagram`, pick `Program_ExcelToGrasshopper_Test.xlsm`, accept worksheet `ProgramInfo`, then optionally select a closed planar boundary.
5. Run `Create2DFloorplanFixture` for sample floorplan input, or select the baked bubble circles.
6. Run `Generate2DFloorplan`.
7. Run `Test2DFloorplanEngine` for the full engine and bake self-test.

The project references Rhino 8 `netcore` `RhinoCommon.dll`, Grasshopper `KangarooSolver.dll` (Copy Local = false), and the ClosedXML NuGet package. Grasshopper must be installed so Kangaroo2 can load at runtime.

## Bubble object user text

Select closed circle curves and set these keys with Rhino's `SetUserText` command:

- `UID` — required, unique room identifier.
- `Area` — required, positive area in square feet.
- `Label` — optional display label; defaults to `UID`.
- `Dept` — optional department; defaults to `Unassigned`.
- `FloorPref` — optional positive preferred floor; `0` or blank means unpinned.
- `Shared` — optional `true/false`; shared rooms are kept beside `AdjacentTo`.
- `AdjacentTo` — optional UID of the primary adjacency parent.
- `DoorWidth` — optional width in feet; defaults to the command setting.

The circle center supplies the bubble location. Its radius is retained as source metadata but room area is controlled by `Area`. Object color becomes the room color.

## Output

Geometry is created under:

- `Floorplan::Floor N::Rooms`
- `Floorplan::Floor N::Labels`
- `Floorplan::Floor N::Corridors::Branch`
- `Floorplan::Floor N::Corridors::Trunk`
- `Floorplan::Floor N::Doors`
- `Floorplan::Floor N::Diagnostics`

Room walls are individual lines with door-width gaps. Each door consists of a leaf line (open position) and a 90-degree swing arc. Shared-access rooms receive a direct door where they share a wall with their parent; otherwise they receive a corridor door with a warning. Objects on each floor are grouped together.

The port preserves the source pipeline's current limitation: floor capacity is area-based. Final containment inside an arbitrary non-rectangular floor-plate boundary is not implemented.
