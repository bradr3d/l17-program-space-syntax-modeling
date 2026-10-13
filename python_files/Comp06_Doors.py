#! python3
# ==============================================================================
# COMPONENT 06  |  DOOR PASS  (corridor doors + direct-adjacency doors)
# ------------------------------------------------------------------------------
# Rhino 8 / Grasshopper CPython script component.
# Places a door POINT for every room. Two door types:
#
#   CORRIDOR DOOR : at the midpoint of the room wall that fronts a corridor. The
#                   wall is chosen from Comp04's access_edge (authoritative), but
#                   VALIDATED against the nearest corridor: if another corridor is
#                   closer to the building center on a different wall, the door
#                   moves to that wall (favouring interior circulation).
#
#   DIRECT DOOR   : for rooms flagged "Shared Access" in Comp01 (restroom/storage
#                   -> classroom). If the room's adjacency PARENT is a spatial
#                   neighbour (shares a wall), the door goes in that SHARED WALL
#                   and NO corridor door is made -- the room connects directly to
#                   its parent, not the corridor.
#
# Doors are POINTS at the opening midpoint.
#
# ------------------------------------------------------------------------------
# INPUTS
#   rooms       : Curve | Tree | from 04 (room rectangles, per floor)
#   room_uid    : str   | Tree | from 04 room_uid_out
#   room_dept   : int   | Tree | from 04 room_dept_out
#   access_edge : int   | Tree | from 04 (0=+X 1=+Y 2=-X 3=-Y)
#   branch_cl   : Curve | Tree | from 05 branch centerlines
#   trunk_cl    : Curve | Tree | from 05 trunk centerlines
#
#   uids_flat   : str   | List | from 01 'uid'   (index-aligned to shared)
#   shared      : bool  | List | from 01 'shared_out'
#   adj_source  : str   | List | from 01 (edge tails, uid)
#   adj_target  : str   | List | from 01 (edge heads, uid)
#
#   unit_system : str   | Item | "feet" or "meters"
#   wall_tol    : float | Item | max gap to treat two rooms as sharing a wall. Default 1.0
#   door_search : float | Item | max distance from a wall to a corridor to accept it. Default 20.0
#   run         : bool  | Item | solve toggle
#
# ------------------------------------------------------------------------------
# OUTPUTS (GRAFTED PER FLOOR, branch = floor rank)
#   doors       : Point3d | Tree | all door points
#   corr_doors  : Point3d | Tree | corridor doors only
#   direct_doors: Point3d | Tree | direct-adjacency doors only
#   door_uid    : str     | Tree | uid of the room each door serves
#   door_kind   : str     | Tree | "corridor" or "direct"
#   info        : str     | Item | diagnostics
#
# ------------------------------------------------------------------------------
# CONVENTIONS / GOTCHAS
#   - "#! python3" required. Rhino.Geometry throughout. Tree Access on Trees.
#   - shared flag joined by uid from Comp01 (Comp03/04 untouched).
#   - access_edge codes: 0=+X (right) 1=+Y (top) 2=-X (left) 3=-Y (bottom).
#   - A door is a Point3d at the wall midpoint of the chosen edge.
# ==============================================================================

import Rhino
import Rhino.Geometry as rg
import System
import math
from Grasshopper import DataTree
from Grasshopper.Kernel.Data import GH_Path


def _get(name, default):
    return globals()[name] if (name in globals() and globals()[name] is not None) else default


def _unit_scale(sys_name):
    name = (sys_name or "feet").strip().lower()
    src_m = 1.0 if name.startswith("m") else 0.3048
    doc = Rhino.RhinoDoc.ActiveDoc
    try:
        umeters = Rhino.RhinoMath.UnitScale(Rhino.UnitSystem.Meters, doc.ModelUnitSystem)
    except Exception:
        umeters = 1.0
    return src_m * umeters


def _branches(tree):
    out = []
    if tree is None:
        return out
    try:
        paths = list(tree.Paths)
    except Exception:
        return out
    for p in paths:
        out.append((p, list(tree.Branch(p))))
    return out


def _bbox(crv):
    bb = crv.GetBoundingBox(True)
    return bb.Min, bb.Max


def _edge_midpoint(mn, mx, edge):
    """Midpoint of the given edge. 0=+X 1=+Y 2=-X 3=-Y."""
    cx = (mn.X + mx.X) * 0.5
    cy = (mn.Y + mx.Y) * 0.5
    if edge == 0:    # +X (right)
        return rg.Point3d(mx.X, cy, 0.0)
    if edge == 1:    # +Y (top)
        return rg.Point3d(cx, mx.Y, 0.0)
    if edge == 2:    # -X (left)
        return rg.Point3d(mn.X, cy, 0.0)
    return rg.Point3d(cx, mn.Y, 0.0)   # -Y (bottom)


def _all_edge_mids(mn, mx):
    return {e: _edge_midpoint(mn, mx, e) for e in range(4)}


# ---- outputs ---------------------------------------------------------------
doors = DataTree[object]()
corr_doors = DataTree[object]()
direct_doors = DataTree[object]()
door_uid = DataTree[object]()
door_kind = DataTree[object]()
info = ""


def _build():
    unit_system = _get("unit_system", "feet")
    lin = _unit_scale(unit_system)
    wall_tol = float(_get("wall_tol", 1.0)) * lin
    door_search = float(_get("door_search", 20.0)) * lin

    rm_b = _branches(_get("rooms", None))
    if not rm_b:
        return "Waiting: no rooms tree from Component 04."
    ru_b = dict((str(p), v) for p, v in _branches(_get("room_uid", None)))
    ae_b = dict((str(p), v) for p, v in _branches(_get("access_edge", None)))
    bcl_b = dict((str(p), v) for p, v in _branches(_get("branch_cl", None)))
    tcl_b = dict((str(p), v) for p, v in _branches(_get("trunk_cl", None)))

    # flat joins from Comp01
    uids_flat = list(_get("uids_flat", []))
    shared_flat = list(_get("shared", []))
    shared_by_uid = {}
    for i in range(min(len(uids_flat), len(shared_flat))):
        shared_by_uid[uids_flat[i]] = bool(shared_flat[i])
    asrc = list(_get("adj_source", []))
    atgt = list(_get("adj_target", []))
    target_of = {}   # uid -> its adjacency target uid
    for s, t in zip(asrc, atgt):
        target_of[s] = t

    n_corr = 0
    n_direct = 0
    n_shared_no_parent = 0

    for (path, room_list) in rm_b:
        key = str(path)
        rank = path.Indices[0] if path.Indices else 0
        pout = GH_Path(rank)
        n = len(room_list)
        if n == 0:
            continue

        uids = ru_b.get(key, ["?%d" % i for i in range(n)])
        aes = ae_b.get(key, [1] * n)

        # room bboxes + centroid for this floor
        boxes = []
        cxs = []
        cys = []
        for i in range(n):
            mn, mx = _bbox(room_list[i])
            boxes.append((mn, mx))
            cxs.append((mn.X + mx.X) * 0.5)
            cys.append((mn.Y + mx.Y) * 0.5)
        floor_cx = sum(cxs) / n
        floor_cy = sum(cys) / n
        floor_center = rg.Point3d(floor_cx, floor_cy, 0.0)

        # corridor centerlines on this floor (branch + trunk)
        corridors = list(bcl_b.get(key, [])) + list(tcl_b.get(key, []))

        # uid -> room index (for direct-door parent lookup)
        idx_of_uid = {}
        for i in range(n):
            idx_of_uid[uids[i]] = i

        def _nearest_corridor_dist(pt):
            best = None
            for c in corridors:
                cp = c.ClosestPoint(pt, door_search)
                if cp[0]:
                    d = pt.DistanceTo(c.PointAt(cp[1]))
                    if best is None or d < best:
                        best = d
            return best

        for i in range(n):
            mn, mx = boxes[i]
            uid = uids[i]
            is_shared = shared_by_uid.get(uid, False)

            # ---- DIRECT DOOR (shared-access rooms) ------------------------
            if is_shared and uid in target_of:
                parent = target_of[uid]
                if parent in idx_of_uid:
                    pj = idx_of_uid[parent]
                    pmn, pmx = boxes[pj]
                    # find the shared wall: the edge of THIS room closest to the
                    # parent room within wall_tol (overlapping/touching).
                    shared_edge = _shared_wall_edge(mn, mx, pmn, pmx, wall_tol)
                    if shared_edge is not None:
                        dp = _edge_midpoint(mn, mx, shared_edge)
                        doors.Add(dp, pout)
                        direct_doors.Add(dp, pout)
                        door_uid.Add(uid, pout)
                        door_kind.Add("direct", pout)
                        n_direct += 1
                        continue   # shared room done -> no corridor door
                    else:
                        n_shared_no_parent += 1
                        # falls through to corridor door as a fallback

            # ---- CORRIDOR DOOR --------------------------------------------
            # start from access_edge; validate against nearest corridor.
            ae = int(aes[i]) if i < len(aes) else 1
            mids = _all_edge_mids(mn, mx)
            # candidate: access_edge midpoint
            best_edge = ae
            best_pt = mids[ae]
            best_d = _nearest_corridor_dist(best_pt)
            # check the other edges; prefer one whose nearest corridor is closer
            # to the building center AND within door_search.
            for e in range(4):
                pt = mids[e]
                d = _nearest_corridor_dist(pt)
                if d is None:
                    continue
                if best_d is None:
                    best_d, best_edge, best_pt = d, e, pt
                    continue
                # prefer a corridor-adjacent edge; tie-break toward building center
                pt_to_center = pt.DistanceTo(floor_center)
                best_to_center = best_pt.DistanceTo(floor_center)
                if (d < best_d - 1e-6) or (abs(d - best_d) <= 1.0 and pt_to_center < best_to_center):
                    best_d, best_edge, best_pt = d, e, pt

            doors.Add(best_pt, pout)
            corr_doors.Add(best_pt, pout)
            door_uid.Add(uid, pout)
            door_kind.Add("corridor", pout)
            n_corr += 1

    lines = ["Component 06 | Door Pass",
             "Unit system: {}   model-units/input: {:.4f}".format(unit_system, lin),
             "Corridor doors: {}   Direct doors: {}".format(n_corr, n_direct),
             "Shared rooms without adjacent parent (fell back to corridor): {}".format(n_shared_no_parent),
             "Door = point at wall midpoint; access_edge validated vs nearest corridor."]
    return "\n".join(lines)


def _shared_wall_edge(mn, mx, pmn, pmx, tol):
    """Which edge of THIS room (mn,mx) abuts the parent room (pmn,pmx) within tol?
    Returns 0=+X 1=+Y 2=-X 3=-Y or None. Requires overlap along the shared edge."""
    # +X edge touches parent's -X edge
    if abs(mx.X - pmn.X) <= tol and _overlap(mn.Y, mx.Y, pmn.Y, pmx.Y) > 0:
        return 0
    # -X edge touches parent's +X edge
    if abs(mn.X - pmx.X) <= tol and _overlap(mn.Y, mx.Y, pmn.Y, pmx.Y) > 0:
        return 2
    # +Y edge touches parent's -Y edge
    if abs(mx.Y - pmn.Y) <= tol and _overlap(mn.X, mx.X, pmn.X, pmx.X) > 0:
        return 1
    # -Y edge touches parent's +Y edge
    if abs(mn.Y - pmx.Y) <= tol and _overlap(mn.X, mx.X, pmn.X, pmx.X) > 0:
        return 3
    return None


def _overlap(a0, a1, b0, b1):
    """Length of 1D overlap between [a0,a1] and [b0,b1]."""
    return max(0.0, min(a1, b1) - max(a0, b0))


_do_run = True if ('run' not in globals() or run is None) else bool(run)
if _do_run:
    info = _build()
else:
    info = "Idle: set run=True to place doors."
