# Generative Floor Plan Pipeline — Technical Handoff

**Scope:** fixes and a floor-assignment refactor across Components 01–04 of the Excel → Grasshopper floor plan generator.
**Status:** Stages 1–3 of the refactor are complete and verified offline. Stage 4 (plate-outline containment) is in design, with an unresolved architectural question described at the end.
**Environment:** Rhino 8 / Grasshopper, CPython script components (`#! python3`), `openpyxl` for the Excel read.

---

## 1. Pipeline overview

The pipeline turns a room program spreadsheet (`Program_ExcelToGrasshopper_Test.xlsm`, Sheet2) into a corridor-organized floor plan. Each component is a Grasshopper CPython script with typed inputs and outputs; downstream components join data by **uid** (`"{DeptNo}#{instance}"`, e.g. `1.1#0`) rather than by list position.

| Comp | Role | Key outputs |
|---|---|---|
| 01 Reader | Reads Excel, explodes Qty into instances, wires directed adjacency | `uid`, `label`, `area`, `floor_pref`, `shared_out`, `adj_source`, `adj_target` |
| 02 Bubble Solver | Force-relaxation bubble diagram (adjacency springs + repulsion) | `centers`, `radii`, `uid_out`, `dept_out` |
| 03 Tiler | **Assigns floors**, k-means clusters per floor, tiles rooms to exact area | `rooms`, `room_uid`, `room_lbl`, `room_floor`, `cluster_id` |
| 04 Corridor Organizer | Re-lays rooms into double-loaded branch/trunk corridor layout | `rooms`, `room_lbl`, `access_edge`, `branch_ctr`, `trunk_ctr` |
| 05 Corridor Width | Offsets corridor skeleton into ribbons, traffic-widened trunks | `branch_corr`, `trunk_corr` |
| 06 Doors | Corridor doors per room; direct doors for "Shared Access" rooms | `doors`, `door_kind` |

Adjacency is **directed**: a room's `Conn. Primary` column names the room it pulls toward. Instances are wired pairwise by index (instance *k* of A → instance *min(k, Nb−1)* of B).

A **Shared Access** room (restroom, vestibule, storage) connects directly to its adjacency parent through a shared wall instead of opening onto a corridor. Comp06 can only place that direct door if the two rooms physically share a wall — this requirement drives several of the fixes below.

### Data-flow change made in this work

```
BEFORE:  01 (floor) → 02 (grafts per floor) → 03 (clusters in floor) → 04/05/06
AFTER:   01 (floor_pref) → 02 (ONE global cloud) → 03 (ASSIGNS floor → grafts) → 04/05/06
```

Floor used to be an *input* to the bubble solve; it is now an *output* of the tiler. Comp03 still emits trees grafted one branch per floor, so the contract with Comps 04–06 is unchanged.

---

## 2. Changes by component

### 2.1 Comp04 — bubble split mode and adjacency-preserving row packing

**Symptom.** Rooms stacked in an even, width-balanced pattern regardless of the `split_mode = "bubble"` setting. Shared-access rooms landed away from their parents (e.g. Classroom-K `1.1` was not beside its restroom `1.1.1` or vestibule `1.1.2`), so Comp06 could not place direct doors.

**Root cause.** `split_mode` was a dead variable: it was read from the input and echoed in the diagnostics, but no code branched on it. The single code path that always ran sorted each branch's rooms by width descending (`pool.sort(key=lambda sr: -sr["w"])`) and dealt them greedily into the two rows of the branch to balance row length. That destroyed both the solver's bubble order and every adjacency chain.

**Fix.** `split_mode` now controls behavior. `"even"` preserves the legacy width-balanced packing for comparison. `"bubble"` (default) does the following. Rooms are ordered into adjacency chains: each non-shared room becomes a chain root, and its shared-access descendants follow it depth-first (classroom → restroom → vestibule). Roots are ordered left-to-right by solver x-position, so the branch still tracks the bubble diagram. Chains are packed as atomic groups — if a parent plus its children won't fit the current row, the whole group wraps. Consecutive rows are paired into branches in order, with no width re-sort. Shared rooms are exempt from big-room extraction so they are never pulled away from a parent. A new `shared` input (from Comp01 `shared_out`) and a room-level `parent_of` map built from `adj_source`/`adj_target` supply the relationships.

**Verification.** Against the real spreadsheet, every `1.1#k ↔ 1.1.1#k ↔ 1.1.2#k` pair shares a vertical wall with 3 ft of overlap in bubble mode, including when corridor breaks force a mid-cluster wrap. Even mode reproduces the original bug (no pairs adjacent), confirming the switch is live.

**Known trade-off.** Bubble mode no longer width-balances the two rows of a branch, so runs can be longer and thinner. In an isolated single-cluster test the floor aspect reached ~18:1 versus ~4:1 in even mode. `max_corridor_len` is the lever that caps run length; chain-respecting row balancing is a possible follow-up.

### 2.2 Comp04 — room labels

**Request.** `room_lbl` should emit the Comp03 label format — `"{DeptNo} {ProgramElement} ({i}/{N})"`, e.g. `1.1 Classroom - K (1/4)` — instead of echoing the uid.

**Implementation.** Comp04 previously received no label data. A new Tree input `lbl_in` takes Comp03's `room_lbl`; a `label_by_uid` map is built from it, and the output looks up each room's label by uid, falling back to the uid when missing. Because the join is by uid, labels survive all of Comp04's reordering.

**Bug found: input/output name collision.** The input was first named `room_lbl`, identical to the output. The script declares the output as a module-level `DataTree`, so `_get("room_lbl")` returned the *empty output tree* instead of the wired input. The input was renamed `lbl_in`. This is the same class of bug that led Comp01 to name its output `shared_out`: **a GH script param's input and output names must differ.**

**Robustness.** After the rename, the user still saw uids on canvas, indicating the lookup was still returning nothing. The lookup was hardened to accept a proper Tree, a flat List (Access set to List), or a Tree whose branch keys don't match the uid tree (e.g. `{0;0}` vs `{0}`), falling back to flatten-and-pair-by-order. A diagnostic line (`Labels loaded from lbl_in: N`) now makes a zero-load visible. Root cause on the canvas was not confirmed; the Access setting on the param was the leading suspect.

### 2.3 Comp01 — floor becomes a preference (Stage 1)

The `Flr` column is now a **preference**, not an assignment. A new output `floor_pref` is `0` for no preference (blank, non-numeric, or ≤ 0) and a positive floor number otherwise. Previously a blank cell was silently coerced to floor 1, which under the new scheme would have masqueraded as a floor-1 pin. The `Flr` column is now optional; if absent, every room is unpinned. The legacy `floor` output is retained unchanged (blank → 1) only so the old Comp02 wiring kept solving during the transition; it is now unused. New diagnostics report pinned vs unpinned counts and net SF per preferred floor. On the test file: floor 1 = 101 rooms / 32,292 SF, floor 2 = 50 rooms / 15,800 SF, total 151 rooms / 48,092 SF (no blanks in the current file).

**Gotcha.** The `Dept No` column contains Excel **array formulas**. Opening the .xlsm with openpyxl in write mode and saving drops the cached values, so a subsequent `data_only=True` read returns `None` for every dept. Never round-trip this workbook through openpyxl in write mode.

### 2.4 Comp02 — single global bubble cloud (Stage 2)

All floor logic was removed: the `floor` input, the `k_floor` cohesion term, per-floor repulsion grouping, the `separated_view` / `spread_axis` / `floor_gap` display offsets, and per-floor grafting. The cross-floor "pull as if stacked" workaround existed only to compensate for floors being separated during the solve and disappeared with them. Output is now a single branch `{0}`.

Repulsion is now global. On 151 rooms that is 11,325 pairs per iteration versus roughly 6,350 when grouped by floor (~1.8× the work); the solve runs in about 0.7 s offline at 400 iterations. Because the single cloud is denser, the hard de-overlap pass no longer converged at its old default of 120 sweeps (residual ≈ 0.03 ft); the default was raised to 300, which converges to ≈ 0.0097 ft (tolerance 0.01 ft).

Comp04 reads solver positions into a uid-keyed dict across all branches, so it is compatible with the single-branch output.

### 2.5 Comp03 — floor assignment engine (Stage 3)

**New inputs.** `floor_pref` (List, Comp01), `shared` (List, Comp01 `shared_out`), `boundary` (Curve, optional), `plate_w` / `plate_l` (used if no boundary curve), `floor_efficiency` (default 0.65, one value for all floors), `max_floors` (default 20, safety stop). **New outputs.** `room_floor` (assigned floor per room) and `plate_rect` (plate echoed at each floor origin).

**Algorithm.** Comp03 flattens Comp02's cloud, then proceeds in six steps.

1. **Atomic chains.** A union-find joins each shared-access room with its adjacency parent (transitively), so each classroom/restroom/vestibule chain becomes one indivisible group. Splitting a chain across floors would break Comp06's direct doors.
2. **Capacity.** `capacity = plate_area × floor_efficiency`, identical for every floor. Plate area comes from `AreaMassProperties` of the boundary curve, or `plate_w × plate_l`.
3. **Pinned chains** go to their preferred floor. If full, they overflow **down first** (pref−1 … 1), then **up**, and each displacement is logged.
4. **Unpinned chains** fill floors bottom-up. An empty floor is seeded with the largest chain that fits; subsequent picks maximize affinity = `w_adj · (adjacency ties to rooms already on the floor) + w_prox · (bubble proximity to the floor centroid) + w_dept · (same-dept share of the floor)`, reusing the weights that already drive k-means clustering.
5. **Oversized chains** (area > one plate's capacity) each get their own floor with an overage warning, per the product decision.
6. **Stop condition.** Floor count is unbounded but capped by `max_floors`, plus a no-progress guard.

Each assigned floor is then tiled with the pre-existing clustering and row-packing logic, one branch per floor. A bounding-box fit check compares each floor's extent to the plate's bbox and **reports only** — Stage 3 budgets by area; geometric fitting is Stage 4.

**Bugs found during implementation.** First, a name collision: the capacity tracker was named `used`, which collided with a local of the same name in the k-means remap inside the tiling loop (`AttributeError: 'list' object has no attribute 'get'`); renamed to `floor_used`. Second, silent room loss: on a deliberately tiny plate, 43 of 151 rooms disappeared with no visible warning. Pinned chains that fit nowhere under the cap were logged but never tracked, and the log was truncated to 12 lines. Both unplaced paths (pinned and unpinned) are now tracked, and any shortfall produces a top-line `*** WARNING: N of 151 ROOMS NOT PLACED ***`. Oversized chains now also respect `max_floors`.

**Verification.**

| Plate (ft) | Efficiency | Floors | Rooms placed | Split chains |
|---|---|---|---|---|
| 150 × 100 | 0.65 | 5 | 151 / 151 | 0 |
| 200 × 150 | 0.65 | 3 | 151 / 151 | 0 |
| 260 × 200 | 0.70 | 2 | 151 / 151 | 0 |
| 300 × 250 | 0.65 | 2 | 151 / 151 | 0 |
| 400 × 300 | 0.70 | 2 | 151 / 151 | 0 |

At 260 × 200, floor 1 received exactly the 101 floor-1-preferred rooms and floor 2 exactly the 50 floor-2 rooms, with zero overflow. The program forms 93 atomic chains, 31 of them multi-room. On a 60 × 40 plate, the two oversized rooms (`6.1#0` at 4,800 SF, `9.1#0` at 2,575 SF) were correctly given their own floors; raising `max_floors` to 40 placed all 151 rooms.

---

## 3. Canvas wiring changes

| Component | Change |
|---|---|
| Comp01 | Add output `floor_pref` (List) |
| Comp02 | Delete inputs `floor`, `separated_view`, `spread_axis`, `floor_gap`, `k_floor` (dead if left wired) |
| Comp03 | Add inputs `floor_pref`, `shared`, `boundary`, `plate_w`, `plate_l`, `floor_efficiency`, `max_floors`; add outputs `room_floor`, `plate_rect` |
| Comp04 | Add input `shared` (List, from Comp01 `shared_out`); add input `lbl_in` (Tree, from Comp03 `room_lbl`) |

Expected on first run: Comp03's bbox report will show `OVERFLOWS BBOX` on most floors. That is correct for Stage 3.

---

## 4. Stage 4 — plate-outline containment (open)

**Requirement.** Rooms must fit inside the actual boundary curve, including non-rectangular plates (L-shapes, curved edges) — not just its bounding box.

**Key finding: Comp04 re-lays everything.** Comp04 has no knowledge of the plate. It rebuilds the entire layout from Comp03's cluster assignments using its own `envelope_aspect` shelf-wrap logic. Any containment enforced only in Comp03's tiling would therefore be discarded. Containment has to be enforced where the final geometry is produced — in Comp04, or in Comp03 with Comp04 changed to preserve Comp03's block positions. This is the first decision for the team.

**Proposed approach (not yet built).** Rasterize the boundary into an occupancy grid using `Curve.Contains(point, plane, tol)` (closed planar curve required), with cell size tied to a fraction of room depth. Place cluster blocks largest-first at the first position where the block — inflated by half the trunk gap — covers only free interior cells. If a block fits nowhere, re-shape it by increasing its row count (narrower and taller) before giving up.

**Open questions.**

1. **Where containment lives** — Comp04 (final layout) or Comp03 with Comp04 made placement-preserving.
2. **Spill behavior.** Geometric packing will fit less than the area budget suggests, so floors can be area-feasible but geometrically overfull. Options: spill unfit rooms to the next floor (a feedback loop from packing back into Stage 3 assignment, iterated to stability), or place with a warning.
3. **Perimeter setback** — whether rooms sit flush to the curve (boundary = exterior wall face) or inset.
4. **Re-shaping aggressiveness** — whether blocks may change proportions to fill awkward regions, or only be repositioned.
5. Longer term: per-floor plates (the `boundary` input should eventually accept a list or tree).

---

## 5. Offline testing approach

All verification was run outside Rhino by stubbing the Rhino/Grasshopper APIs and `exec`-ing the component scripts with a globals dict that stands in for the wired inputs. Real data came from the spreadsheet via openpyxl, chained 01 → 02 → 03 (and 04). Checks included room count and uid uniqueness, shared-chain same-floor integrity, and wall adjacency (shared edge within tolerance with positive overlap).

The stub pattern:

```python
import sys, types
Rhino = types.ModuleType("Rhino"); rg = types.ModuleType("Rhino.Geometry")
class Pt:
    def __init__(s, x, y, z=0.0): s.X, s.Y, s.Z = float(x), float(y), float(z)
rg.Point3d = Pt          # + Circle, Plane, Rectangle3d, Line, AreaMassProperties ...
Rhino.RhinoMath = types.SimpleNamespace(UnitScale=lambda a, b: 1.0)
Rhino.RhinoDoc = types.SimpleNamespace(ActiveDoc=types.SimpleNamespace(ModelUnitSystem="Feet"))
sys.modules["Rhino"] = Rhino; sys.modules["Rhino.Geometry"] = rg

class GH_Path:
    def __init__(s, *i): s.Indices = list(i)
    def __str__(s): return "{" + ";".join(map(str, s.Indices)) + "}"
class DataTree:
    def __init__(s): s.d = {}
    def Add(s, item, path): s.d.setdefault(str(path), []).append(item)
    @property
    def Paths(s):   # must preserve ALL indices, not just the first
        return [GH_Path(*[int(x) for x in k.strip("{}").split(";")]) for k in s.d]
    def Branch(s, p): return s.d.get(str(p), [])
# register Grasshopper, Grasshopper.Kernel.Data (GH_Path), System.Drawing.Color similarly

G = {"uid": [...], "area": [...], "run": True}      # wired inputs
exec(compile(open("Comp02_BubbleSolver.py").read(), "C02", "exec"), G)
print(G["info"])                                    # outputs land in G
```

One harness pitfall surfaced: an early `DataTree.Paths` stub kept only the first path index, silently collapsing `{0;0}` to `{0}` and producing false failures. Stubs need to be as faithful as the behavior under test.

Limitations: stubs cover geometry construction and tree plumbing, not Rhino's geometric kernels. `Curve.Contains`, true curve area, and on-canvas param Access settings must be verified in Grasshopper — the unresolved `lbl_in` issue in 2.2 is an example of something offline tests cannot catch.

---

## 6. Grasshopper CPython gotchas collected

A script param's input and output names must be different; a module-level output `DataTree` shadows a same-named input. Local variable names inside `_build()` can silently shadow each other across long functions (the `used` collision). Input params consumed as trees must have **Access = Tree**; otherwise the value is not a `DataTree` and a defensive `_branches()` helper silently returns nothing. Branch-key depth mismatches (`{0;0}` vs `{0}`) break keyed joins — join by uid wherever possible. Never write the .xlsm with openpyxl (array formulas lose cached values). Any stage that can drop items should count what it dropped and surface it at the top of `info`, not in a truncated log.
