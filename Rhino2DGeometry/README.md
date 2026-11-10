# Rhino 2D Geometry

Rhino 8 plug-in (`net7.0-windows`) with two main commands:

- `ExcelToBubbleDiagram` — reads a program workbook, sizes bubbles from net SF, and relaxes a constrained bubble diagram with Kangaroo2.
- `Generate2DFloorplan` — converts selected metadata-tagged bubble circles into 2D room walls, corridor ribbons, wall openings, door leaves, and swing arcs.

Generation also creates capped 3D room masses at a default height of **14 feet**, keeps stairs and corridors flat, and exports the current 2D plan to `floorplan.svg` in the Week 1 Build root. Floors remain side by side.

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

### Excel department breaks and omitted rooms

`ExcelToBubbleDiagram` ignores blank rows and rows with `Room Qty/ Units` equal to zero, including the zero-filled department separators produced by workbook formulas. Import continues with the following department. Named zero-quantity spaces are reported as skipped in the command output; they contribute no bubbles, area, or connections. Connections targeting an omitted space are reported as unmatched.

Missing, nonnumeric, negative, or nonfinite quantities still fail validation with the Excel row number. Rooms with a positive quantity still require a finite, positive `Net SF/ Units`; excluded rows do not require an area. The standalone tests cover this behavior using temporary fixtures and both root workbooks.

Collision corrections are averaged across each bubble's overlapping neighbors, with contact counts included in the goal weights. This keeps dense clusters from receiving an excessive summed movement. Invalid or runaway solver positions stop generation before geometry is baked. Managed tests cover dense collisions and coincident centers; the optional `--rhino` test also solves the root workbook and verifies 151 baked circles.

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
- `RoomType` — optional `Room` or `Staircase` (case insensitive). Explicit values take precedence. Otherwise whole-word labels such as `Stair 1`, `Stairs A`, and `Staircase 2` identify stairs. `Upstairs office` remains a room.

The circle center supplies the bubble location. Its radius is retained as source metadata but room area is controlled by `Area`. Object color becomes the room color.

You can window-select the bubble diagram, including its connection lines and boundary. `Generate2DFloorplan` ignores selected objects without `UID` or `Area` user text and prints one ignored-object count. Objects carrying either room metadata key must still be valid circles with complete metadata; malformed room objects and duplicate UIDs remain validation errors.

## Output

Geometry is created under:

- `Floorplan::Floor N::Rooms`
- `Floorplan::Floor N::Rooms3D` — capped room masses, preserving colors and metadata, including `HeightFeet`.
- `Floorplan::Floor N::Stairs` — closed flat staircase footprints.
- `Floorplan::Floor N::Labels`
- `Floorplan::Floor N::Corridors::Branch`
- `Floorplan::Floor N::Corridors::Trunk`
- `Floorplan::Floor N::Doors`
- `Floorplan::Floor N::Diagnostics`

Room walls are individual lines with door-width gaps. Each door consists of a leaf line (open position) and a 90-degree swing arc. Shared-access rooms receive a direct door where they share a wall with their parent; otherwise they receive a corridor door with a warning. Objects on each floor are grouped together.

The port preserves the source pipeline's current limitation: floor capacity is area-based. Final containment inside an arbitrary non-rectangular floor-plate boundary is not implemented.

## Rebuild and install after changes

Close Rhino before rebuilding if this plug-in is loaded: Rhino retains the loaded assembly until it exits. Open PowerShell in the **Week 1 Build root** and run:

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" restore .\Rhino2DGeometry\Rhino2DGeometry.csproj
& "$env:ProgramFiles\dotnet\dotnet.exe" build .\Rhino2DGeometry\Rhino2DGeometry.csproj -c Release --no-restore
& "$env:ProgramFiles\dotnet\dotnet.exe" run --project .\Rhino2DGeometry.Tests\Rhino2DGeometry.Tests.csproj -c Release
```

Open Rhino 8, run `PluginManager`, and install `Rhino2DGeometry\bin\Release\Rhino2DGeometry.rhp` from this project. Keep the accompanying DLLs in that output folder. For an already installed plug-in, restart Rhino after rebuilding so it loads the new version. Use the project output location so SVG export can locate the project root.

## Generate geometry in Rhino

1. Run `ExcelToBubbleDiagram`, choose the root-level `.xlsm` or `.xlsx` workbook, accept `ProgramInfo`, and finish the bubble options.
2. Select the generated metadata-tagged room circles.
3. Run `Generate2DFloorplan`, accept or adjust the settings, and choose an output origin.
4. Inspect 2D geometry on `Rooms`, `Corridors`, and `Doors`; 3D masses on `Rooms3D`; flat stairs on `Stairs`. Shaded display helps inspect the masses. Toggle `Rooms3D` visibility for an unobstructed 2D view.
5. Open `floorplan.svg` in the Week 1 Build root. Rhino prints its absolute path. Each generation replaces this file and exports only that generation, including all floors, walls, corridors, doors, and labels. Diagnostics and volumes are excluded. SVG export errors are reported separately and preserve the Rhino geometry.

For testing without Excel, run `Create2DFloorplanFixture`, select its room circles, then run `Generate2DFloorplan`. Run `Test2DFloorplanEngine` for geometry and bake checks; this adds test geometry to the active document, so use a scratch document. A single Undo reverses geometry from one `Generate2DFloorplan` run; the exported SVG is a separate disk file.

The volume step creates solid room masses from complete footprints, without doorway cutouts. It does not create stair flights or stack floors vertically. Heights do not come from Excel. `RoomVolumeSettings` exposes `DefaultRoomHeightFeet`, `HeightOverridesFeet` keyed by room UID, and `ResolveHeightFeet` for future Eto integration; no height UI is added yet. Values must be finite and positive and are converted from feet into the Rhino document units.

On systems that support Rhino running inside a test process, the optional command below runs the geometry, SVG, and bake tests without opening a Rhino window. It requires a working Rhino runtime/license; if initialization fails, use `Test2DFloorplanEngine` in Rhino instead.

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" run --project .\Rhino2DGeometry.Tests\Rhino2DGeometry.Tests.csproj -c Release -- --rhino
```
