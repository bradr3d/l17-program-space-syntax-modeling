# DAL decisions

Short log of Data Access Layer decisions and why. Newest at the bottom of each section.

## Setup (2026-10-13)

- **ClosedXML pinned to 0.105.0** (MIT). Its API changes between minors, so bump it on purpose and re-run the golden test.
- **Projects target `net8.0`.** `Psm.Data` has no Rhino, Grasshopper or Eto references. A test walks the reference graph to enforce this.
- **Prototype source:** `../ExcelToProgramModel` (read-only). `ClaudeHandoff/Comp01_Reader.py` is the reference.

## Reading the workbook

- **Cached values only.** Cells are read through `IXLCell.CachedValue`, never `Value`. The file is read into memory with `FileShare.ReadWrite` and handed to ClosedXML as a read-only stream, so nothing can be saved back. A test checks the file's SHA-256 before and after import.
- **Sheet resolution:** user-supplied name → `ProgramInfo` → first sheet with a "Dept No" + "Program Element" header row (searched in the first 30 rows). **The "code name Sheet2" step is skipped** because ClosedXML 0.105 does not expose VBA code names. The header search finds the same sheet.
- **Header matching** uses exact normalized names plus an explicit alias list. Normalizing trims, lower-cases, collapses spaces and removes spaces around `/`. There is no "contains" fallback.
- **Numeric ID cells use their number format.** ClosedXML reports built-in formats only by id: `ProgramInfo!A61` shows `NumberFormatId = 2` with an empty `Format` string. `ExcelCells` maps the ECMA-376 built-in ids to format codes, and `IdText` reads min/max decimals from the code. The result: `7.1` in `0.00` reads as `7.10`.
- **W004 policy:** a plain General number such as `1.1` gets no diagnostic, because Excel shows the user the same text. Info is used when the format changed the text and recovered the id (A61, I62). Warning is used when the format hides digits or can't be read.
- **Color = K-cell solid fill.** Theme colors are resolved without tint. If K has no fill, L is used; if neither exists, the color of the first filled room in the same department, else mid-grey (W007). **W008 is aggregated:** when L holds the same value on every row and disagrees with every fill (the stale `findcolor` cache on the test file), one sheet-level W008 is raised instead of 72.
- **Department names** come from ProjectSheet whole-number rows. When an id repeats (dept 8), the header with room rows under it wins, and the repeat is reported as **I003** (a new info code, added to `DiagnosticCodes`).
- **Project metadata** (ProjectSheet A1:B5) is stored in `source.project`, so it is outside the hash.

## Parsing rules

- **Two passes.** Pass 1 checks each row on its own. A bad *required* field (id, name, qty, net SF) drops the row and counts it in `invalidRowsSkipped`. A bad *optional* field (Total SF, stations, Flr fraction, bad RGB when there is no fill) raises E005 and the field is left null, but the row is kept. Pass 2 checks relationships over the rows that passed.
- **Row classes**, all counted in `ImportSummary`: room / blank / divider (invalid id, and name blank or `0`) / department header (whole-number id with a name, the same convention as ProjectSheet; this lets a CSV name its departments) / invalid.
- **E001:** the first row keeps a duplicated id. Later rows are dropped and reported. No `7.1b` suffixes.
- **E003 also covers a Conn. Primary that points to the row itself.**
- **W003:** a connection to an absorbed (Qty 0) target skips that room's edges. It counts in `unresolvedRoomTypes` and `edgesSkipped`.
- **Names are trimmed** (`"Tutoring "` → `Tutoring`). The prototype kept the trailing space in labels.
- **`Aspect`** stores the raw text. `"None"` and blank become null. Its number format is ignored.
- **`Flr`:** blank, non-numeric or ≤ 0 → null (no preference). A positive non-integer → E005.
- **`RoomType.TotalSf` was added** (not in the original model sketch). It keeps the sheet's Total Program SF so the 7.1 Reception question (17 vs 298) can be answered later without re-reading Excel.
- **Instance key** is `"{id}#{k}"` with **0-based k** (`1.1#0`), matching the prototype, handoff docs and prototype screenshots. The label is `"{id} {name} ({k+1}/{N})"`.
- **Edges:** instance k of A → instance `min(k, Nb−1)` of B. The kind is `SharedWall` when the *source* row has Shared Access, otherwise `Adjacent`.
- **`ImportResult.Program`** is null only when nothing usable could be read (missing required column or no table). With errors it is a best effort and `Ok` is false.

## JSON

- One `PsmJson` options object: camelCase, camelCase string enums, nulls written, relaxed escaping (so `&` stays readable), and LF line endings on every OS.
- Lists are sorted by id, segment-wise numerically (`1.1 < 1.1.1 < 1.2 < 7.10 < 10.1`). Keys sort by id, then index. Diagnostics sort by sheet, row, column, code.
- `programHash = "sha256:" + hex(SHA-256(compact JSON without source, diagnostics, summary, programHash))`.
- Colors are `{ "r", "g", "b" }`.
- **Schema version 1.** `ProgramMigrations` holds the upgrade chain, which is empty for now. A newer file is rejected with a diagnostic, not an exception.

## Tests

- **The test workbook and `program.golden.json` are committed** in `Fixtures/` (2026-10-13: nothing in them is confidential). `PSM_TEST_WORKBOOK` can override the workbook path. Regenerate the golden file with `UPDATE_GOLDEN=1` and review the diff.
- `program.sample.csv` / `program.sample.json` (classroom 1.1 chain + Art 2.1 chain) are a small fixture for teammates.

## Open questions (answers go here)

- Is Flr a firm rule or a preference? *(Prototype: preference. Implemented as preference.)*
- Confirm Shared Access = "opens directly onto its Conn. Primary target".
- 7.1 Reception area: 17 SF or 298 SF? *(Currently 17, with W001.)*
- Child qty > parent qty (`6.1.2`, `7.10.1`): how should instances pair? *(Currently clamped.)*
- Aspect Ratio meaning ("1 to 1": exact or within tolerance?) and units of Room Width Min. *(Assumed ft.)*
- Will rooms ever need more than one weighted adjacency?
- Is translating behavior from the prototype source acceptable for a clean-room rebuild?
