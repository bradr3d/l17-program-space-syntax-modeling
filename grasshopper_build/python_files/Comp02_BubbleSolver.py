#! python3
# ==============================================================================
# COMPONENT 02  |  BUBBLE DIAGRAM SOLVER  (force relaxation, per-floor)
# ------------------------------------------------------------------------------
# Rhino 8 / Grasshopper CPython script component.
# Consumes the flat room lists from Component 01 and relaxes them into a
# non-overlapping bubble diagram. Adjacency pulls (directed) draw a room toward
# its Conn.Primary target; a repulsion term prevents overlap; a floor term keeps
# each floor as a coherent cluster. Cross-floor adjacency pulls "as if stacked"
# (the X separation between floors is ignored for that pull) so vertical cores
# can align later.
#
# A Boolean toggle switches between STACKED (all floors overlaid at origin) and
# SEPARATED (floors offset along an axis) purely for visual presentation. The
# physics are identical in both; only the drawn X (or Y) offset differs.
#
# ------------------------------------------------------------------------------
# INPUTS
#   uid          : str   | List | from Comp 01 (index-aligned room lists)
#   area         : float | List | Net SF per instance
#   floor        : int   | List | floor number per instance
#   color        : Colour| List | per instance
#   label        : str   | List | per instance
#   dept         : int   | List | per instance (passthrough)
#   adj_source   : str   | List | edge tails  (uid)
#   adj_target   : str   | List | edge heads  (uid)
#   depts        : int   | List | passthrough for Comp 03
#
#   unit_system  : str   | Item | "feet" or "meters" -- units the AREAS/inputs are in
#   iterations   : int   | Item | fixed relaxation steps. Default 400.
#   deoverlap_iters : int| Item | hard de-overlap sweeps after main solve.
#                                  Default 120. Guarantees near-zero overlap;
#                                  raise if info reports "CAP HIT".
#   separated_view : bool| Item | True=floors offset on axis; False=overlaid
#   spread_axis  : int   | Item | 0=X, 1=Y offset direction for separated view
#   floor_gap    : float | Item | gap (in unit_system) between floor blocks
#   k_adj        : float | Item | adjacency spring stiffness. Default 0.08
#   k_repel      : float | Item | overlap repulsion stiffness. Default 0.85
#   k_floor      : float | Item | floor-cohesion pull to floor centroid. Default 0.015
#   pad          : float | Item | min gap between bubble edges (unit_system). Default 2.0
#   seed         : int   | Item | RNG seed for initial layout. Default 42
#   run          : bool  | Item | solve toggle
#
# ------------------------------------------------------------------------------
# OUTPUTS  (GRAFTED PER FLOOR: one branch per floor, ordered by floor number)
#   centers   : Point3d | Tree | bubble centers (Rhino units)
#   circles   : Circle  | Tree | bubble circles for display
#   radii     : float   | Tree | bubble radii (Rhino units)
#   labels    : str     | Tree | labels, branch-aligned to centers
#   colors    : Colour  | Tree | colors, branch-aligned
#   uid_out   : str     | Tree | uids, branch-aligned (name-based lookups downstream)
#   dept_out  : int     | Tree | dept token, branch-aligned
#   env_rect  : Rect(Curve)| Tree | per-floor bounding rectangle (envelope)
#   adj_lines : Line    | Tree | adjacency connectors, grafted per floor of the SOURCE
#   depts     : int     | List | passthrough (sorted unique dept tokens)
#   max_overlap : float | Item | worst residual bubble overlap (input units).
#                                 ~0 means clean. Watch this before Part 2.
#   info      : str     | Item | diagnostics
#
# ------------------------------------------------------------------------------
# CONVENTIONS / GOTCHAS
#   - "#! python3" required. Rhino.Geometry types throughout.
#   - Type hints: uid/label str, area float, floor int, color Colour (List Access).
#   - Areas convert unit_system -> Rhino file unit via a scale factor so bubble
#     radii are in true model units. r = sqrt(area_model / pi).
#   - Cross-floor adjacency: pull computed on FLOOR-LOCAL coords (X offset removed),
#     so stacked-equivalent alignment holds regardless of view mode.
#   - use int(math.ceil(...)) where integer counts are needed.
#   - isinstance() (not hasattr) for type checks.
# ==============================================================================

import Rhino
import Rhino.Geometry as rg
import System
import math
import random
from Grasshopper import DataTree
from Grasshopper.Kernel.Data import GH_Path


# ---- unit conversion: unit_system -> Rhino file unit ------------------------
def _unit_scale(sys_name):
    """Return model units per 1 linear input unit.
    Areas are SF or SM depending on unit_system; we convert the LINEAR
    dimension (sqrt of area) into the active Rhino file unit."""
    name = (sys_name or "feet").strip().lower()
    if name.startswith("m"):
        src_m = 1.0            # meters -> meters
    else:
        src_m = 0.3048         # feet -> meters
    # active Rhino unit expressed in meters
    doc = Rhino.RhinoDoc.ActiveDoc
    try:
        umeters = Rhino.RhinoMath.UnitScale(Rhino.UnitSystem.Meters, doc.ModelUnitSystem)
        # umeters = model units per meter
    except Exception:
        umeters = 1.0
    return src_m * umeters      # model units per 1 input linear unit


def _area_to_radius(a_input, lin_scale):
    """area given in (input-unit)^2 -> radius in model units."""
    if a_input is None or a_input <= 0:
        a_input = 1.0
    a_model = a_input * (lin_scale * lin_scale)
    return math.sqrt(a_model / math.pi)


# ---- guards / defaults ------------------------------------------------------
def _get(name, default):
    return globals()[name] if (name in globals() and globals()[name] is not None) else default


centers = DataTree[object]()
circles = DataTree[object]()
radii = DataTree[object]()
labels = DataTree[object]()
colors = DataTree[object]()
uid_out = DataTree[object]()
dept_out = DataTree[object]()
env_rect = DataTree[object]()
adj_lines = DataTree[object]()
depts = []
max_overlap = 0.0
info = ""


def _solve():
    global depts, max_overlap
    lines = []

    _uid = list(_get("uid", []))
    _area = list(_get("area", []))
    _floor = list(_get("floor", []))
    _color = list(_get("color", []))
    _label = list(_get("label", []))
    _dept = list(_get("dept", []))
    _asrc = list(_get("adj_source", []))
    _atgt = list(_get("adj_target", []))
    _depts_in = list(_get("depts", []))

    n = len(_uid)
    if n == 0:
        return "Waiting: no rooms from Component 01."
    # index-alignment guard
    for nm, lst in [("area", _area), ("floor", _floor)]:
        if len(lst) != n:
            return "ERROR: '{}' length {} != uid length {}.".format(nm, len(lst), n)

    unit_system = _get("unit_system", "feet")
    iters = int(_get("iterations", 400))
    separated = bool(_get("separated_view", True))
    spread_axis = int(_get("spread_axis", 0))
    floor_gap_in = float(_get("floor_gap", 30.0))
    k_adj = float(_get("k_adj", 0.08))
    k_repel = float(_get("k_repel", 0.85))
    k_floor = float(_get("k_floor", 0.015))
    pad_in = float(_get("pad", 2.0))
    seed = int(_get("seed", 42))

    lin = _unit_scale(unit_system)
    pad = pad_in * lin
    floor_gap = floor_gap_in * lin

    # ---- build per-instance arrays ----------------------------------------
    rad = [_area_to_radius(_area[i], lin) for i in range(n)]
    fl = [int(_floor[i]) for i in range(n)]
    idx_of = {}                      # uid -> index
    for i in range(n):
        idx_of[_uid[i]] = i

    floors = sorted(set(fl))
    fl_rank = {f: r for r, f in enumerate(floors)}
    # precompute membership ONCE (main loop rebuilt this every iter -> slow on 151)
    members_of = {f: [i for i in range(n) if fl[i] == f] for f in floors}

    deoverlap_iters = int(_get("deoverlap_iters", 120))
    max_step = None  # set after radii known

    # ---- initial placement: seeded ring per floor (local coords) ----------
    random.seed(seed)
    px = [0.0] * n
    py = [0.0] * n
    for f in floors:
        members = members_of[f]
        # rough radius of the floor cloud
        tot_a = sum(math.pi * rad[i] * rad[i] for i in members)
        R = math.sqrt(tot_a / math.pi) * 1.3 + 1.0
        for i in members:
            ang = random.uniform(0, 2 * math.pi)
            rr = random.uniform(0, R)
            px[i] = rr * math.cos(ang)
            py[i] = rr * math.sin(ang)

    # adjacency edges as index pairs (skip any that don't resolve)
    edges = []
    for s, t in zip(_asrc, _atgt):
        if s in idx_of and t in idx_of:
            edges.append((idx_of[s], idx_of[t]))

    # ---- relaxation loop (fixed iterations) -------------------------------
    # All physics run in FLOOR-LOCAL coordinates (each floor centered near its
    # own origin); cross-floor adjacency therefore pulls "as if stacked".
    for _ in range(max(1, iters)):
        fx = [0.0] * n
        fy = [0.0] * n

        # 1) adjacency springs (directed: source pulled toward target)
        for (a, b) in edges:
            dx = px[b] - px[a]
            dy = py[b] - py[a]
            d = math.hypot(dx, dy)
            rest = rad[a] + rad[b] + pad
            if d < 1e-6:
                continue
            # pull source toward target proportional to (d - rest)
            f = k_adj * (d - rest)
            ux, uy = dx / d, dy / d
            fx[a] += f * ux
            fy[a] += f * uy
            # mild reciprocal so the pair converges (target nudged half back)
            fx[b] -= 0.5 * f * ux
            fy[b] -= 0.5 * f * uy

        # 2) non-overlap repulsion (only within the same floor)
        for f in floors:
            members = members_of[f]
            m = len(members)
            for ii in range(m):
                a = members[ii]
                for jj in range(ii + 1, m):
                    b = members[jj]
                    dx = px[b] - px[a]
                    dy = py[b] - py[a]
                    d = math.hypot(dx, dy)
                    min_d = rad[a] + rad[b] + pad
                    if d < 1e-6:
                        # jitter apart
                        dx, dy, d = random.uniform(-1, 1), random.uniform(-1, 1), 1.0
                    if d < min_d:
                        overlap = (min_d - d)
                        ux, uy = dx / d, dy / d
                        f2 = k_repel * overlap
                        fx[a] -= f2 * ux
                        fy[a] -= f2 * uy
                        fx[b] += f2 * ux
                        fy[b] += f2 * uy

        # 3) floor cohesion: gentle pull to each floor centroid
        for f in floors:
            members = members_of[f]
            if not members:
                continue
            cx = sum(px[i] for i in members) / len(members)
            cy = sum(py[i] for i in members) / len(members)
            for i in members:
                fx[i] += k_floor * (cx - px[i])
                fy[i] += k_floor * (cy - py[i])

        # integrate with a per-step clamp so a bubble can't leap through a
        # neighbor in one iteration (overshoot is the main source of frozen
        # overlap). Cap the move at ~half the smallest radius.
        if max_step is None:
            max_step = 0.5 * min(rad)
        for i in range(n):
            sx, sy = fx[i], fy[i]
            slen = math.hypot(sx, sy)
            if slen > max_step and slen > 1e-9:
                sx *= max_step / slen
                sy *= max_step / slen
            px[i] += sx
            py[i] += sy

    # ---- DE-OVERLAP PASS (guarantees near-zero overlap) -------------------
    # Runs repulsion ONLY (no springs, no cohesion), moving each overlapping
    # pair exactly half the overlap distance directly apart. With nothing
    # pulling them back, overlap collapses to ~0 in a few dozen iterations.
    # We iterate until the worst overlap is under tolerance or we hit the cap.
    tol = 0.01 * lin                      # 0.01 input-units of slop, in model units
    max_ov = 0.0
    for _di in range(max(0, deoverlap_iters)):
        worst = 0.0
        for f in floors:
            members = members_of[f]
            m = len(members)
            for ii in range(m):
                a = members[ii]
                for jj in range(ii + 1, m):
                    b = members[jj]
                    dx = px[b] - px[a]
                    dy = py[b] - py[a]
                    d = math.hypot(dx, dy)
                    min_d = rad[a] + rad[b] + pad
                    if d < 1e-9:
                        ang = random.uniform(0, 2 * math.pi)
                        dx, dy, d = math.cos(ang), math.sin(ang), 1.0
                    if d < min_d:
                        overlap = min_d - d
                        if overlap > worst:
                            worst = overlap
                        # push each bubble half the overlap directly apart
                        ux, uy = dx / d, dy / d
                        shift = 0.5 * overlap
                        px[a] -= shift * ux
                        py[a] -= shift * uy
                        px[b] += shift * ux
                        py[b] += shift * uy
        max_ov = worst
        if worst <= tol:
            break
    max_overlap = max_ov / lin if lin else max_ov   # report in input units

    # ---- re-center each floor cloud to its local origin -------------------
    for f in floors:
        members = members_of[f]
        if not members:
            continue
        cx = sum(px[i] for i in members) / len(members)
        cy = sum(py[i] for i in members) / len(members)
        for i in members:
            px[i] -= cx
            py[i] -= cy

    # ---- compute view offset per floor ------------------------------------
    # Local coords are stacked-equivalent. For separated view we add a per-floor
    # offset along spread_axis, sized to each floor's width plus floor_gap.
    def _floor_extent(members, axis):
        lo = min((px[i] if axis == 0 else py[i]) - rad[i] for i in members)
        hi = max((px[i] if axis == 0 else py[i]) + rad[i] for i in members)
        return lo, hi

    offset = {f: 0.0 for f in floors}
    if separated:
        run = 0.0
        for f in floors:
            members = members_of[f]
            if not members:
                continue
            lo, hi = _floor_extent(members, spread_axis)
            width = hi - lo
            # place this floor so its low edge sits at 'run'
            offset[f] = run - lo
            run += width + floor_gap

    # ---- emit grafted per-floor outputs -----------------------------------
    z = 0.0
    for f in floors:
        rank = fl_rank[f]
        path = GH_Path(rank)
        members = members_of[f]

        off = offset[f]
        ox = off if (separated and spread_axis == 0) else 0.0
        oy = off if (separated and spread_axis == 1) else 0.0

        # world position resolver (local + view offset)
        def wx(i):
            return px[i] + ox

        def wy(i):
            return py[i] + oy

        for i in members:
            pt = rg.Point3d(wx(i), wy(i), z)
            centers.Add(pt, path)
            circles.Add(rg.Circle(pt, rad[i]), path)
            radii.Add(rad[i], path)
            labels.Add(_label[i] if i < len(_label) else _uid[i], path)
            colors.Add(_color[i] if i < len(_color) else System.Drawing.Color.Gray, path)
            uid_out.Add(_uid[i], path)
            dept_out.Add(_dept[i] if i < len(_dept) else -1, path)

        # envelope rectangle for this floor
        if members:
            lox = min(wx(i) - rad[i] for i in members)
            hix = max(wx(i) + rad[i] for i in members)
            loy = min(wy(i) - rad[i] for i in members)
            hiy = max(wy(i) + rad[i] for i in members)
            plane = rg.Plane(rg.Point3d(lox, loy, z), rg.Vector3d.ZAxis)
            rect = rg.Rectangle3d(plane, hix - lox, hiy - loy)
            env_rect.Add(rect.ToNurbsCurve(), path)

    # ---- adjacency lines, grafted by SOURCE floor -------------------------
    def _world(i):
        f = fl[i]
        off = offset[f]
        ox = off if (separated and spread_axis == 0) else 0.0
        oy = off if (separated and spread_axis == 1) else 0.0
        return rg.Point3d(px[i] + ox, py[i] + oy, 0.0)

    for (a, b) in edges:
        rank = fl_rank[fl[a]]
        adj_lines.Add(rg.Line(_world(a), _world(b)), GH_Path(rank))

    # ---- passthrough depts -------------------------------------------------
    if _depts_in:
        depts = list(_depts_in)
    else:
        depts = sorted(set(_dept)) if _dept else []

    lines.append("Component 02 | Bubble Solver")
    lines.append("Rooms: {}   Floors: {}   Edges: {}".format(n, len(floors), len(edges)))
    lines.append("Unit system: {}   model-units/input: {:.4f}".format(unit_system, lin))
    lines.append("View: {}   axis: {}   iters: {}".format(
        "SEPARATED" if separated else "STACKED", "XY"[spread_axis] if spread_axis in (0, 1) else "?", iters))
    lines.append("k_adj={}  k_repel={}  k_floor={}  pad={}".format(k_adj, k_repel, k_floor, pad_in))
    _clean = max_overlap <= (0.01 + 1e-9)
    lines.append("De-overlap: {}   max residual overlap: {:.4f} {}  {}".format(
        "converged" if _clean else "CAP HIT (raise deoverlap_iters)",
        max_overlap, unit_system,
        "OK" if _clean else "<-- still overlapping"))
    for f in floors:
        cnt = len(members_of[f])
        lines.append("  Floor {} : {} rooms  (branch {{{}}})".format(f, cnt, fl_rank[f]))
    return "\n".join(lines)


_do_run = True if ('run' not in globals() or run is None) else bool(run)
if _do_run:
    info = _solve()
else:
    info = "Idle: set run=True to solve."
