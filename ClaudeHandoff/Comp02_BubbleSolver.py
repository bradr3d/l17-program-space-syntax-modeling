#! python3
# ==============================================================================
# COMPONENT 02  |  BUBBLE DIAGRAM SOLVER  (force relaxation, single cloud)
# ------------------------------------------------------------------------------
# Rhino 8 / Grasshopper CPython script component.
# Consumes the flat room lists from Component 01 and relaxes them into ONE
# non-overlapping bubble diagram. Adjacency pulls (directed) draw a room toward
# its Conn.Primary target; a repulsion term prevents overlap.
#
# FLOORS ARE NOT KNOWN HERE. This component solves the program as a single
# global adjacency cloud; Component 03 assigns rooms to floors from that cloud
# using floor preference + plate capacity + proximity/adjacency/dept affinity.
# Because there are no floors, the old floor-cohesion term (k_floor), the
# per-floor repulsion grouping, and the STACKED/SEPARATED view offsets are all
# gone -- along with the cross-floor "pull as if stacked" workaround they
# required. Floor separation now begins in Component 03.
#
# ------------------------------------------------------------------------------
# INPUTS
#   uid          : str   | List | from Comp 01 (index-aligned room lists)
#   area         : float | List | Net SF per instance
#   color        : Colour| List | per instance
#   NOTE: the old 'floor' input is GONE. Floor preference travels straight from
#         Comp01 'floor_pref' to Comp03; it plays no part in the bubble solve.
#         Delete the floor / separated_view / spread_axis / floor_gap / k_floor
#         params from the component -- leaving them wired is harmless but dead.
#   label        : str   | List | per instance
#   dept         : int   | List | per instance (passthrough)
#   adj_source   : str   | List | edge tails  (uid)
#   adj_target   : str   | List | edge heads  (uid)
#   depts        : int   | List | passthrough for Comp 03
#
#   unit_system  : str   | Item | "feet" or "meters" -- units the AREAS/inputs are in
#   iterations   : int   | Item | fixed relaxation steps. Default 400.
#   deoverlap_iters : int| Item | hard de-overlap sweeps after main solve.
#                                  Default 300. Raised from 120 because the cloud
#                                  is now global (denser) rather than per-floor;
#                                  120 leaves ~0.03 ft residual on 151 rooms.
#                                  Raise further if info reports "CAP HIT".
#   k_adj        : float | Item | adjacency spring stiffness. Default 0.08
#   k_repel      : float | Item | overlap repulsion stiffness. Default 0.85
#   pad          : float | Item | min gap between bubble edges (unit_system). Default 2.0
#   seed         : int   | Item | RNG seed for initial layout. Default 42
#   run          : bool  | Item | solve toggle
#
# ------------------------------------------------------------------------------
# OUTPUTS  (SINGLE BRANCH {0} -- one global cloud, no floors)
#   centers   : Point3d | Tree | bubble centers (Rhino units)
#   circles   : Circle  | Tree | bubble circles for display
#   radii     : float   | Tree | bubble radii (Rhino units)
#   labels    : str     | Tree | labels, branch-aligned to centers
#   colors    : Colour  | Tree | colors, branch-aligned
#   uid_out   : str     | Tree | uids, branch-aligned (name-based lookups downstream)
#   dept_out  : int     | Tree | dept token, branch-aligned
#   env_rect  : Rect(Curve)| Tree | bounding rectangle of the whole cloud
#   adj_lines : Line    | Tree | adjacency connectors (single branch)
#   depts     : int     | List | passthrough (sorted unique dept tokens)
#   max_overlap : float | Item | worst residual bubble overlap (input units).
#                                 ~0 means clean. Watch this before Part 2.
#   info      : str     | Item | diagnostics
#
# ------------------------------------------------------------------------------
# CONVENTIONS / GOTCHAS
#   - "#! python3" required. Rhino.Geometry types throughout.
#   - Type hints: uid/label str, area float, color Colour (List Access).
#   - Areas convert unit_system -> Rhino file unit via a scale factor so bubble
#     radii are in true model units. r = sqrt(area_model / pi).
#   - Repulsion is now GLOBAL (all pairs) rather than grouped per floor, so the
#     pair count rises from sum(n_f^2)/2 to n^2/2. On 151 rooms that is ~1.8x the
#     work per iteration; drop 'iterations' if the solve feels slow.
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
    for nm, lst in [("area", _area)]:
        if len(lst) != n:
            return "ERROR: '{}' length {} != uid length {}.".format(nm, len(lst), n)

    unit_system = _get("unit_system", "feet")
    iters = int(_get("iterations", 400))
    k_adj = float(_get("k_adj", 0.08))
    k_repel = float(_get("k_repel", 0.85))
    pad_in = float(_get("pad", 2.0))
    seed = int(_get("seed", 42))

    lin = _unit_scale(unit_system)
    pad = pad_in * lin

    # ---- build per-instance arrays ----------------------------------------
    rad = [_area_to_radius(_area[i], lin) for i in range(n)]
    idx_of = {}                      # uid -> index
    for i in range(n):
        idx_of[_uid[i]] = i

    # single global membership -- every room is in one cloud
    members = list(range(n))

    deoverlap_iters = int(_get("deoverlap_iters", 300))
    max_step = None  # set after radii known

    # ---- initial placement: one seeded disc for the whole program ---------
    random.seed(seed)
    px = [0.0] * n
    py = [0.0] * n
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

        # 2) non-overlap repulsion (GLOBAL -- every pair, no floor grouping)
        for a in range(n):
            for b in range(a + 1, n):
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

        # (floor cohesion removed: there are no floors at this stage)

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
        for a in range(n):
            for b in range(a + 1, n):
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

    # ---- re-center the single cloud on the origin -------------------------
    if members:
        cx = sum(px[i] for i in members) / len(members)
        cy = sum(py[i] for i in members) / len(members)
        for i in members:
            px[i] -= cx
            py[i] -= cy

    # ---- emit ONE branch {0} ----------------------------------------------
    # No floors here, so no view offsets and no per-floor grafting. Comp03 reads
    # this flat cloud, assigns floors, and does the grafting + separation.
    z = 0.0
    path = GH_Path(0)
    for i in members:
        pt = rg.Point3d(px[i], py[i], z)
        centers.Add(pt, path)
        circles.Add(rg.Circle(pt, rad[i]), path)
        radii.Add(rad[i], path)
        labels.Add(_label[i] if i < len(_label) else _uid[i], path)
        colors.Add(_color[i] if i < len(_color) else System.Drawing.Color.Gray, path)
        uid_out.Add(_uid[i], path)
        dept_out.Add(_dept[i] if i < len(_dept) else -1, path)

    # envelope rectangle for the whole cloud
    if members:
        lox = min(px[i] - rad[i] for i in members)
        hix = max(px[i] + rad[i] for i in members)
        loy = min(py[i] - rad[i] for i in members)
        hiy = max(py[i] + rad[i] for i in members)
        plane = rg.Plane(rg.Point3d(lox, loy, z), rg.Vector3d.ZAxis)
        rect = rg.Rectangle3d(plane, hix - lox, hiy - loy)
        env_rect.Add(rect.ToNurbsCurve(), path)

    # ---- adjacency lines (single branch) ----------------------------------
    for (a, b) in edges:
        adj_lines.Add(rg.Line(rg.Point3d(px[a], py[a], 0.0),
                              rg.Point3d(px[b], py[b], 0.0)), path)

    # ---- passthrough depts -------------------------------------------------
    if _depts_in:
        depts = list(_depts_in)
    else:
        depts = sorted(set(_dept)) if _dept else []

    lines.append("Component 02 | Bubble Solver (single global cloud)")
    lines.append("Rooms: {}   Edges: {}   (floors assigned downstream in Comp03)".format(n, len(edges)))
    lines.append("Unit system: {}   model-units/input: {:.4f}".format(unit_system, lin))
    lines.append("Iterations: {}   global repulsion pairs/iter: {:,}".format(
        iters, n * (n - 1) // 2))
    lines.append("k_adj={}  k_repel={}  pad={}".format(k_adj, k_repel, pad_in))
    _clean = max_overlap <= (0.01 + 1e-9)
    lines.append("De-overlap: {}   max residual overlap: {:.4f} {}  {}".format(
        "converged" if _clean else "CAP HIT (raise deoverlap_iters)",
        max_overlap, unit_system,
        "OK" if _clean else "<-- still overlapping"))
    if members:
        _w = (max(px[i] + rad[i] for i in members) - min(px[i] - rad[i] for i in members)) / lin
        _h = (max(py[i] + rad[i] for i in members) - min(py[i] - rad[i] for i in members)) / lin
        lines.append("Cloud extent: {:.0f} x {:.0f} {}  (single branch {{0}})".format(
            _w, _h, unit_system))
    return "\n".join(lines)


_do_run = True if ('run' not in globals() or run is None) else bool(run)
if _do_run:
    info = _solve()
else:
    info = "Idle: set run=True to solve."
