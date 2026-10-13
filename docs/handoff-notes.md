# Notes for teammates outside the DAL

## For everyone: how to get a program

```csharp
using Psm.Data.Excel;      // ExcelProgramSource
using Psm.Data.Csv;        // CsvProgramSource
using Psm.Data.Json;       // JsonProgramSource
using Psm.Data.Model;

ImportResult r = new ExcelProgramSource(path).Load();   // or Csv/Json
if (!r.Ok) { /* show r.Diagnostics to the architect: Code, Severity, Message, Sheet, Row, Column */ }
BuildingProgram p = r.Program!;
```

- No Excel? Load `tests/Psm.Data.Tests/Fixtures/program.sample.json` with `JsonProgramSource`. It contains the classroom 1.1 chain and the Art 2.1 chain: 8 room types, 20 rooms, 20 edges.
- Per room: `p.Instances` gives `Key` (`"1.1#0"`), `TypeId` and `Label`. Look up `p.FindType(TypeId)` for name, department, `NetSf` (per room, already *not* × Qty), `Color` (RGB), `FloorPref` (null = no preference) and `SharedAccess`.
- `p.Edges` are directed `FromKey → ToKey` (the source pulls toward the target). `Kind = SharedWall` means the source opens directly onto the target with no corridor door.
- `SharedChains.Compute(p)` returns the atomic chains (union-find over SharedWall edges) that must stay on one floor. On the test workbook that is 93 chains, 31 of them multi-room, matching the prototype.
- Absorbed (Qty 0) room types are in `p.RoomTypes` with `Absorbed = true`, but have no instances.

## Solver / Scoring owners

- The DAL does **not** assign floors or place rooms. Solver settings (plate size, efficiency, max floors, corridor widths, seeds, weights) will be stored by the DAL as an opaque, versioned object inside a scheme record (not built yet). Please send me the settings shape when it is stable.
- `7.11.1 Tutoring` (6 rooms) has no edges, because its target `7.11` has Qty 0 (W003).

## Rhino-adapter owner

- **Not built yet:** `StoreSerializer` (store ↔ string) and the user-text key constants. When they exist, you wire them into `WriteDocument`/`ReadDocument` and `SetUserString`. The DAL will not reference RhinoCommon.
- Baked rooms need both **actual SF** and **target SF** in user text (per the reference output image). Target SF = `RoomType.NetSf`.

## UI owner

- Diagnostics are written for architects ("Row 25: Conn. Primary '4.7' does not match any Dept No."). Show `Severity` and `Row` and let the user sort by them. `ImportSummary` has the counts for a status line: rooms, edges, rows skipped and why, and SF per preferred floor.
