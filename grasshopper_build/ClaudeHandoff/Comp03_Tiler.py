#! python3
# ==============================================================================
# COMPONENT 03  |  CLUSTER TILER  (soft bubbles -> hard rectilinear rooms)
# ------------------------------------------------------------------------------
# Rhino 8 / Grasshopper CPython script component.
# Consumes the grafted per-floor bubble layout from Component 02 and converts it
# into hard, rectilinear rooms grouped into clusters, aligned into bands, and
# laid out with reserved corridor channels. Emits corridor-READY geometry
# (access-edge tags + reserved gaps) but NO corridor geometry -- that is Part 2.
#
# PIPELINE CONTRACT (current):
#   01 Reader  -> flat: uid, label, dept, area, floor, color, adj_source/target
#   02 Solver  -> grafted per-floor: centers, radii, labels, colors, uid_out,
#                 dept_out, env_rect  (branch index = floor rank)
#   03 Tiler   -> (this) clustered exact-area rooms, band-aligned, corridor-ready
#
# ------------------------------------------------------------------------------
# METHOD
#   Per floor (each grafted branch):
#     1) CLUSTER rooms via weighted K-means. Feature vector blends:
#          PROXIMITY : solver (x,y) center           * w_prox
#          ADJACENCY : mean position of adjacent uids * w_adj   (adjacency-dominant)
#          DEPARTMENT: categorical dept coordinate    * w_dept
#        k = min(n_clusters, n_rooms); seeded from dept centroids; empties dropped.
#     2) SHAPE each room to EXACT area, aspect flexed within 1:2..2:1, long axis
#        chosen from the room's local spread in the bubble cloud.
#     3) BAND-ALIGN: within a cluster, sort rooms into rows (by Y), order each
#        row by X, stack rows; snap shared edges AFTER shaping. A snap that would
#        change area by more than area_tol (default 5%) is NOT fully applied --
#        a small gap is left instead (area preserved over gap-free).
#     4) LAYOUT clusters in a coarse grid per floor, reserving corridor channels
#        (channel_gap, default ~7 ft) between rows and between clusters.
#     5) TAG each room's access_edge = the face pointing at the nearest reserved
#        channel (0=+X,1=+Y,2=-X,3=-Y). Corridors/doors in Part 2 consume this.
#     6) OFFSET each floor block by a running floor_run accumulator along
#        spread_axis with floor_gap, mirroring the solver's separated view.
#
# ------------------------------------------------------------------------------
# INPUTS
#   centers    : Point3d | Tree | from 02 (grafted per floor)
#   radii      : float   | Tree | from 02
#   labels     : str     | Tree | from 02
#   colors     : Colour  | Tree | from 02
#   uid_out    : str     | Tree | from 02  (name-based lookups; tiler reorders)
#   dept_out   : int     | Tree | from 02
#   areas      : float   | List | from 01 'area'  (flat; looked up by uid)
#   uids_flat  : str     | List | from 01 'uid'   (flat; index-aligned to areas)
#   adj_source : str     | List | from 01 (edge tails, uid)
#   adj_target : str     | List | from 01 (edge heads, uid)
#
#   floor_pref : int     | List | from 01 'floor_pref' (0 = no preference)
#   shared     : bool    | List | from 01 'shared_out' (keeps chains on one floor)
#
#   boundary   : Curve   | Item | floor plate outline from Rhino. Optional if
#                                 plate_w/plate_l are given. Same plate is used
#                                 for every floor (per-floor plates come later).
#   plate_w    : float   | Item | plate width  (unit_system) if no boundary curve
#   plate_l    : float   | Item | plate length (unit_system) if no boundary curve
#   floor_efficiency : float | Item | usable fraction of the plate after corridors
#                                 and structure. Default 0.65. One value, all floors.
#   max_floors : int     | Item | safety stop for the unbounded floor loop. Default 20.
#
#   unit_system: str     | Item | "feet" or "meters" (areas' units)
#   n_clusters : int     | Item | MAX clusters per floor. Default 6.
#   w_prox     : float   | Item | proximity weight. Default 1.0
#   w_adj      : float   | Item | adjacency weight. Default 2.5 (dominant)
#   w_dept     : float   | Item | department weight. Default 1.0
#   area_tol   : float   | Item | alignment budget as area fraction. Default 0.05
#   channel_gap: float   | Item | reserved corridor gap (unit_system). Default 7.0
#   room_gap   : float   | Item | min gap between rooms in a band. Default 0.5
#   spread_axis: int     | Item | 0=X, 1=Y floor offset direction. Default 0
#   floor_gap  : float   | Item | gap between floor blocks (unit_system). Default 30
#   kmeans_iters : int   | Item | K-means iterations. Default 50
#   seed       : int     | Item | RNG seed. Default 42
#   run        : bool    | Item | solve toggle
#
# ------------------------------------------------------------------------------
# OUTPUTS (GRAFTED PER FLOOR, branch = floor rank; index-aligned across room outs)
#   rooms      : Rectangle3d(Curve) | Tree | hard rectilinear rooms
#   room_lbl   : str     | Tree | label per room (branch-aligned)
#   room_uid   : str     | Tree | uid per room  (name-based lookups downstream)
#   room_dept  : int     | Tree | dept token per room
#   room_color : Colour  | Tree | color per room
#   cluster_id : int     | Tree | cluster index per room (within its floor)
#   access_edge: int     | Tree | 0=+X 1=+Y 2=-X 3=-Y face onto nearest channel
#   dev_pct    : float   | Tree | signed area deviation % per room (should be <= tol)
#   cluster_rect : Rectangle3d(Curve) | Tree | bbox per cluster (for Part 2)
#   env_rect   : Rectangle3d(Curve) | Tree | floor envelope
#   room_floor : int     | Tree | assigned floor number per room (branch-aligned)
#   plate_rect : Curve   | Tree | the plate outline echoed at each floor's origin
#   depts      : int     | List | passthrough
#   info       : str     | Item | diagnostics (incl. floor fill % and overflow log)
#
# ------------------------------------------------------------------------------
# FLOOR ASSIGNMENT (Stage 3)
#   Comp02 now emits ONE global bubble cloud; this component decides floors.
#     1) CHAIN GROUPING: a "Shared Access" room is glued to its adjacency parent
#        (transitively: vestibule -> restroom -> classroom). Each chain is an
#        ATOMIC unit -- it can never be split across floors, or Comp06's direct
#        doors would have no adjacent parent.
#     2) CAPACITY: usable = plate_area * floor_efficiency. Same for every floor.
#     3) OVERSIZED: a chain whose own area exceeds one plate's capacity is given
#        its own floor and reported as an overage (it cannot fit anywhere else).
#     4) PINNED: chains with a floor_pref go to that floor. If the floor is full
#        they overflow DOWN first (pref-1, pref-2, ... 1), then UP, and every
#        displacement is logged.
#     5) UNPINNED: floors fill bottom-up. Each floor is seeded with the largest
#        remaining chain, then grown by AFFINITY = w_adj * adjacency ties to
#        rooms already on the floor + w_prox * bubble proximity + w_dept * same-
#        dept mass. Same weights that drive the clustering.
#     6) STOP: unbounded floor count, bounded by max_floors plus a no-progress
#        guard so a pathological chain can never spin the loop.
#   Floors are then tiled exactly as before, one branch per assigned floor.
#
# ------------------------------------------------------------------------------
# CONVENTIONS / GOTCHAS
#   - "#! python3" required. Rhino.Geometry throughout.
#   - Tree inputs on Tree Access; flat inputs on List Access; scalars on Item.
#   - Name-based lookups by uid (the tiler REORDERS rooms vs solver order).
#   - int(math.ceil(...)) for integer counts; isinstance() for type checks.
#   - Exact area first, align after; area_tol is the alignment budget.
# ==============================================================================

import Rhino
import Rhino.Geometry as rg
import System
import math
import random
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


# ---- tree helpers ----------------------------------------------------------
def _branches(tree):
    """Yield (path, [values]) for a GH DataTree, robust to CPython marshaling."""
    out = []
    if tree is None:
        return out
    try:
        paths = list(tree.Paths)
    except Exception:
        return out
    for p in paths:
        vals = list(tree.Branch(p))
        out.append((p, vals))
    return out


# ---- outputs ---------------------------------------------------------------
rooms = DataTree[object]()
room_lbl = DataTree[object]()
room_uid = DataTree[object]()
room_dept = DataTree[object]()
room_color = DataTree[object]()
cluster_id = DataTree[object]()
access_edge = DataTree[object]()
dev_pct = DataTree[object]()
cluster_rect = DataTree[object]()
env_rect = DataTree[object]()
room_floor = DataTree[object]()
plate_rect = DataTree[object]()
depts = []
info = ""


def _plate_metrics(boundary_crv, pw_model, pl_model):
    """Return (area_model, bbox_w, bbox_h, source) for the floor plate.
    Prefers a Rhino boundary curve; falls back to plate_w x plate_l."""
    if boundary_crv is not None:
        try:
            bb = boundary_crv.GetBoundingBox(True)
            bw = bb.Max.X - bb.Min.X
            bh = bb.Max.Y - bb.Min.Y
            amp = rg.AreaMassProperties.Compute(boundary_crv)
            area = abs(amp.Area) if amp is not None else (bw * bh)
            if area > 0:
                return area, bw, bh, "boundary curve"
        except Exception:
            pass
    if pw_model > 0 and pl_model > 0:
        return pw_model * pl_model, pw_model, pl_model, "plate_w x plate_l"
    return 0.0, 0.0, 0.0, "none"


def _kmeans(feats, k, iters, seed):
    """Lloyd's K-means on feature rows (list of tuples). Returns label per row.
    Seeded deterministically; empty clusters are re-seeded to the farthest point."""
    n = len(feats)
    dim = len(feats[0]) if n else 0
    if n == 0 or k <= 1:
        return [0] * n
    k = min(k, n)
    random.seed(seed)

    # ---- seed: k-means++ style spread for stability ----
    cents = [list(feats[random.randrange(n)])]
    while len(cents) < k:
        d2 = []
        for f in feats:
            best = min(sum((f[t] - c[t]) ** 2 for t in range(dim)) for c in cents)
            d2.append(best)
        tot = sum(d2)
        if tot <= 1e-12:
            cents.append(list(feats[random.randrange(n)]))
            continue
        r = random.uniform(0, tot)
        acc = 0.0
        pick = 0
        for i, v in enumerate(d2):
            acc += v
            if acc >= r:
                pick = i
                break
        cents.append(list(feats[pick]))

    labels = [0] * n
    for _ in range(max(1, iters)):
        # assign
        changed = False
        for i, f in enumerate(feats):
            best_c, best_d = 0, None
            for c in range(k):
                d = sum((f[t] - cents[c][t]) ** 2 for t in range(dim))
                if best_d is None or d < best_d:
                    best_d, best_c = d, c
            if labels[i] != best_c:
                changed = True
            labels[i] = best_c
        # update
        for c in range(k):
            members = [feats[i] for i in range(n) if labels[i] == c]
            if not members:
                # re-seed empty cluster to the point farthest from its centroid
                far_i, far_d = 0, -1.0
                for i, f in enumerate(feats):
                    d = sum((f[t] - cents[labels[i]][t]) ** 2 for t in range(dim))
                    if d > far_d:
                        far_d, far_i = d, i
                cents[c] = list(feats[far_i])
                continue
            cents[c] = [sum(m[t] for m in members) / len(members) for t in range(dim)]
        if not changed:
            break
    return labels


def _shape_exact(area_model, long_axis_horizontal):
    """Return (w, h) for a rectangle of EXACT area, aspect within 1:2..2:1.
    Picks a near-square-ish ratio but respects requested long-axis orientation."""
    if area_model <= 0:
        area_model = 1.0
    # target aspect ~ golden-ish but clamped to band; use 1.4 as a pleasant default
    ratio = 1.4  # long:short
    ratio = max(1.0, min(2.0, ratio))
    short = math.sqrt(area_model / ratio)
    long = area_model / short
    if long_axis_horizontal:
        return long, short
    return short, long


def _build():
    global depts
    lines = []

    unit_system = _get("unit_system", "feet")
    lin = _unit_scale(unit_system)
    n_clusters = int(_get("n_clusters", 6))
    w_prox = float(_get("w_prox", 1.0))
    w_adj = float(_get("w_adj", 2.5))
    w_dept = float(_get("w_dept", 1.0))
    area_tol = float(_get("area_tol", 0.05))
    channel_gap = float(_get("channel_gap", 7.0)) * lin
    room_gap = float(_get("room_gap", 0.5)) * lin
    spread_axis = int(_get("spread_axis", 0))
    floor_gap = float(_get("floor_gap", 30.0)) * lin
    kmeans_iters = int(_get("kmeans_iters", 50))
    seed = int(_get("seed", 42))
    floor_efficiency = float(_get("floor_efficiency", 0.65))
    max_floors = int(_get("max_floors", 20))
    plate_w_in = float(_get("plate_w", 0.0) or 0.0)
    plate_l_in = float(_get("plate_l", 0.0) or 0.0)

    # ---- flat lookups from Comp01 -----------------------------------------
    uids_flat = list(_get("uids_flat", []))
    areas_flat = list(_get("areas", []))
    area_by_uid = {}
    for i in range(min(len(uids_flat), len(areas_flat))):
        area_by_uid[uids_flat[i]] = float(areas_flat[i])

    asrc = list(_get("adj_source", []))
    atgt = list(_get("adj_target", []))
    # undirected adjacency map for the clustering signal
    adj_map = {}
    for s, t in zip(asrc, atgt):
        adj_map.setdefault(s, []).append(t)
        adj_map.setdefault(t, []).append(s)

    # ---- grafted inputs from Comp02 ---------------------------------------
    centers_b = _branches(_get("centers", None))
    if not centers_b:
        return "Waiting: no 'centers' tree from Component 02."
    radii_b = dict((str(p), v) for p, v in _branches(_get("radii", None)))
    labels_b = dict((str(p), v) for p, v in _branches(_get("labels", None)))
    colors_b = dict((str(p), v) for p, v in _branches(_get("colors", None)))
    uid_b = dict((str(p), v) for p, v in _branches(_get("uid_out", None)))
    dept_b = dict((str(p), v) for p, v in _branches(_get("dept_out", None)))

    depts_in = list(_get("depts", []))

    floor_run = 0.0
    total_rooms = 0
    total_over = 0
    dept_set = set()

    # ======================================================================
    # FLOOR ASSIGNMENT  (Comp02 now emits ONE global cloud)
    # ======================================================================
    # ---- flatten every branch of the incoming cloud into global arrays ----
    g_ctr = []; g_rad = []; g_lbl = []; g_col = []; g_uid = []; g_dept = []
    for (p, ctrs) in centers_b:
        k = str(p)
        rr_ = radii_b.get(k, [1.0] * len(ctrs))
        lb_ = labels_b.get(k, [""] * len(ctrs))
        cl_ = colors_b.get(k, [System.Drawing.Color.Gray] * len(ctrs))
        uu_ = uid_b.get(k, ["?%d" % i for i in range(len(ctrs))])
        dd_ = dept_b.get(k, [-1] * len(ctrs))
        for i in range(len(ctrs)):
            g_ctr.append(ctrs[i])
            g_rad.append(rr_[i] if i < len(rr_) else 1.0)
            g_lbl.append(lb_[i] if i < len(lb_) else "")
            g_col.append(cl_[i] if i < len(cl_) else System.Drawing.Color.Gray)
            g_uid.append(uu_[i] if i < len(uu_) else "?%d" % i)
            g_dept.append(dd_[i] if i < len(dd_) else -1)
    N = len(g_uid)
    if N == 0:
        return "Waiting: bubble cloud from Component 02 is empty."
    gidx_of = {g_uid[i]: i for i in range(N)}

    # ---- plate capacity ---------------------------------------------------
    plate_area, plate_bw, plate_bh, plate_src = _plate_metrics(
        _get("boundary", None), plate_w_in * lin, plate_l_in * lin)
    if plate_area <= 0:
        return ("Waiting: no floor plate. Wire a 'boundary' curve, or set "
                "plate_w and plate_l.")
    capacity = plate_area * max(1e-6, floor_efficiency)

    # ---- floor preference + shared flags, joined by uid -------------------
    pref_list = list(_get("floor_pref", []))
    shared_list = list(_get("shared", []))
    pref_by_uid = {}
    for i in range(min(len(uids_flat), len(pref_list))):
        try:
            pref_by_uid[uids_flat[i]] = int(pref_list[i])
        except (ValueError, TypeError):
            pref_by_uid[uids_flat[i]] = 0
    shared_by_uid = {}
    for i in range(min(len(uids_flat), len(shared_list))):
        shared_by_uid[uids_flat[i]] = bool(shared_list[i])

    # directed parent (source -> its Conn.Primary target)
    parent_of = {}
    for s, t in zip(asrc, atgt):
        parent_of[s] = t

    # ---- ATOMIC CHAINS: glue each shared room to its parent ---------------
    # union-find over rooms; a shared room unions with its parent, so a whole
    # classroom/restroom/vestibule chain collapses to one group.
    par = {u: u for u in g_uid}

    def _find(u):
        while par[u] != u:
            par[u] = par[par[u]]
            u = par[u]
        return u

    def _union(a, b):
        ra, rb = _find(a), _find(b)
        if ra != rb:
            par[rb] = ra

    for u in g_uid:
        if shared_by_uid.get(u, False):
            p = parent_of.get(u)
            if p in par:
                _union(p, u)

    groups = {}
    for u in g_uid:
        groups.setdefault(_find(u), []).append(u)

    # ---- group attributes -------------------------------------------------
    grp = []
    pref_conflicts = []
    for root, mem in groups.items():
        a_model = sum(area_by_uid.get(u, 100.0) for u in mem) * (lin * lin)
        prefs = [pref_by_uid.get(u, 0) for u in mem]
        nz = sorted(set(p for p in prefs if p > 0))
        gp = nz[0] if nz else 0
        if len(nz) > 1:
            pref_conflicts.append("{} members disagree {} -> using {}".format(
                root, nz, gp))
        # area-weighted bubble centroid
        tw = 0.0; cx = 0.0; cy = 0.0
        for u in mem:
            w_ = area_by_uid.get(u, 100.0)
            i_ = gidx_of[u]
            cx += g_ctr[i_].X * w_; cy += g_ctr[i_].Y * w_; tw += w_
        cx = cx / tw if tw else 0.0
        cy = cy / tw if tw else 0.0
        grp.append(dict(root=root, mem=mem, area=a_model, pref=gp,
                        cx=cx, cy=cy, dept=g_dept[gidx_of[root]],
                        n=len(mem)))

    # ---- assignment -------------------------------------------------------
    floor_of_group = {}        # root -> floor number (1-based)
    floor_used = {}            # floor -> area consumed (capacity tracker)
    overflow_log = []
    overage_log = []
    pinned_unplaced = []       # pinned chains that fit nowhere under the cap

    def _fits(f, a):
        return floor_used.get(f, 0.0) + a <= capacity + 1e-9

    def _place(g, f):
        floor_of_group[g["root"]] = f
        floor_used[f] = floor_used.get(f, 0.0) + g["area"]

    # 1) OVERSIZED chains: cannot fit any plate -> own floor, reported.
    oversized = [g for g in grp if g["area"] > capacity]
    normal = [g for g in grp if g["area"] <= capacity]

    # 2) PINNED chains: preferred floor, overflow DOWN then UP.
    pinned = sorted([g for g in normal if g["pref"] > 0],
                    key=lambda g: (g["pref"], -g["area"]))
    for g in pinned:
        want = g["pref"]
        if _fits(want, g["area"]):
            _place(g, want)
            continue
        placed = False
        for f in range(want - 1, 0, -1):          # DOWN first
            if _fits(f, g["area"]):
                _place(g, f)
                overflow_log.append("{} pref {} -> {} (down, floor {} full)".format(
                    g["root"], want, f, want))
                placed = True
                break
        if not placed:
            f = want + 1
            while f <= max_floors:                 # then UP
                if _fits(f, g["area"]):
                    _place(g, f)
                    overflow_log.append("{} pref {} -> {} (up, no room below)".format(
                        g["root"], want, f))
                    placed = True
                    break
                f += 1
        if not placed:
            pinned_unplaced.append(g)

    # 3) UNPINNED chains: fill bottom-up by affinity.
    free = [g for g in normal if g["pref"] <= 0]

    def _floor_members(f):
        out = []
        for r_, ff in floor_of_group.items():
            if ff == f:
                out.extend(groups[r_])
        return out

    xs_all = [p.X for p in g_ctr]; ys_all = [p.Y for p in g_ctr]
    cloud_span = max((max(xs_all) - min(xs_all)), (max(ys_all) - min(ys_all))) or 1.0

    def _affinity(g, f):
        mem_f = _floor_members(f)
        if not mem_f:
            return 0.0
        mem_set = set(mem_f)
        # adjacency ties from this chain to rooms already on the floor
        ties = 0
        for u in g["mem"]:
            for v in adj_map.get(u, []):
                if v in mem_set:
                    ties += 1
        ties_n = ties / float(max(1, len(g["mem"])))
        # bubble proximity to the floor's centroid (normalized by cloud span)
        fx = sum(g_ctr[gidx_of[u]].X for u in mem_f) / len(mem_f)
        fy = sum(g_ctr[gidx_of[u]].Y for u in mem_f) / len(mem_f)
        d = math.hypot(g["cx"] - fx, g["cy"] - fy)
        prox_n = 1.0 / (1.0 + d / max(1e-9, cloud_span))
        # same-dept mass already on the floor
        same = sum(1 for u in mem_f if g_dept[gidx_of[u]] == g["dept"])
        dept_n = same / float(max(1, len(mem_f)))
        return w_adj * ties_n + w_prox * prox_n + w_dept * dept_n

    f_cur = 1
    guard = 0
    while free and f_cur <= max_floors:
        guard += 1
        if guard > max_floors * (len(grp) + 2):
            overflow_log.append("no-progress guard tripped; stopping fill")
            break
        # anything still fitting on this floor?
        candidates = [g for g in free if _fits(f_cur, g["area"])]
        if not candidates:
            f_cur += 1
            continue
        if not _floor_members(f_cur):
            # empty floor -> seed with the largest chain that fits
            pick = max(candidates, key=lambda g: g["area"])
        else:
            pick = max(candidates, key=lambda g: (_affinity(g, f_cur), g["area"]))
        _place(pick, f_cur)
        free.remove(pick)

    unplaced = list(free) + list(pinned_unplaced)

    # 4) OVERSIZED chains get their own floors on top, still under the cap.
    top = max(list(floor_used.keys()) + [0])
    for g in oversized:
        if top + 1 > max_floors:
            unplaced.append(g)
            continue
        top += 1
        _place(g, top)
        overage_log.append(
            "{} area {:,.0f} {}2 exceeds plate capacity {:,.0f} -> own floor {} "
            "({:.0f}% over)".format(
                g["root"], g["area"] / (lin * lin), unit_system,
                capacity / (lin * lin), top,
                (g["area"] / capacity - 1.0) * 100.0))

    n_unplaced_rooms = sum(g["n"] for g in unplaced)
    for g in unplaced:
        overflow_log.append("{} ({} rooms) UNPLACED - hit max_floors={}".format(
            g["root"], g["n"], max_floors))

    # ---- expand groups back to per-room floor assignment ------------------
    floor_of_uid = {}
    for r_, f in floor_of_group.items():
        for u in groups[r_]:
            floor_of_uid[u] = f
    floors_used = sorted(set(floor_of_uid.values()))
    members_by_floor = {f: [] for f in floors_used}
    for i in range(N):
        f = floor_of_uid.get(g_uid[i])
        if f is not None:
            members_by_floor[f].append(i)

    _fit_report = []

    # ======================================================================
    # TILE each assigned floor (unchanged logic, one branch per floor)
    # ======================================================================
    for rank, fl_no in enumerate(floors_used):
        idxs = members_by_floor[fl_no]
        n = len(idxs)
        if n == 0:
            continue
        ctr_list = [g_ctr[i] for i in idxs]
        rr = [g_rad[i] for i in idxs]
        lb = [g_lbl[i] for i in idxs]
        cl = [g_col[i] for i in idxs]
        uu = [g_uid[i] for i in idxs]
        dd = [g_dept[i] for i in idxs]

        # room records for THIS floor
        recs = []
        for i in range(n):
            pt = ctr_list[i]
            u = uu[i] if i < len(uu) else "?%d" % i
            a_in = area_by_uid.get(u, math.pi * (rr[i] ** 2) / (lin * lin) if i < len(rr) else 100.0)
            recs.append(dict(uid=u, x=pt.X, y=pt.Y,
                             label=lb[i] if i < len(lb) else u,
                             color=cl[i] if i < len(cl) else System.Drawing.Color.Gray,
                             dept=dd[i] if i < len(dd) else -1,
                             area_in=a_in))
            dept_set.add(recs[-1]["dept"])

        # ---- clustering feature vectors -----------------------------------
        # normalize positions to keep weights comparable
        xs = [r["x"] for r in recs]
        ys = [r["y"] for r in recs]
        spanx = (max(xs) - min(xs)) or 1.0
        spany = (max(ys) - min(ys)) or 1.0

        def _adj_mean(u, fallback):
            nbrs = adj_map.get(u, [])
            pts = [(rr2["x"], rr2["y"]) for rr2 in recs for rr2u in [rr2["uid"]] if rr2u in nbrs]
            if not pts:
                return fallback
            return (sum(p[0] for p in pts) / len(pts),
                    sum(p[1] for p in pts) / len(pts))

        feats = []
        for r in recs:
            amx, amy = _adj_mean(r["uid"], (r["x"], r["y"]))
            feats.append((
                w_prox * (r["x"] / spanx),
                w_prox * (r["y"] / spany),
                w_adj * (amx / spanx),
                w_adj * (amy / spany),
                w_dept * float(r["dept"]),
            ))

        k = min(n_clusters, n)
        labels_k = _kmeans(feats, k, kmeans_iters, seed + rank)

        # compact cluster ids (drop empties)
        used = sorted(set(labels_k))
        remap = {c: i for i, c in enumerate(used)}
        for i in range(n):
            recs[i]["cid"] = remap[labels_k[i]]
        n_cl = len(used)

        # ---- shape + band-align each cluster ------------------------------
        # cluster local layouts first (origin-local), then place in a coarse grid.
        cluster_blocks = []  # list of dict: cid, rooms[list], w, h
        for cid in range(n_cl):
            members = [r for r in recs if r["cid"] == cid]
            if not members:
                continue
            # rows by Y (band-sort): split into ceil(sqrt) rows
            members.sort(key=lambda r: (-r["y"], r["x"]))
            nrows = max(1, int(math.ceil(math.sqrt(len(members)))))
            per_row = int(math.ceil(len(members) / float(nrows)))

            # shape each room to exact area
            for r in members:
                a_model = r["area_in"] * (lin * lin)
                # long axis horizontal if the room sits wider than tall in bubble
                r["w"], r["h"] = _shape_exact(a_model, True)
                r["a_model"] = a_model

            # lay rooms into rows, left->right, rows top->bottom
            rows = [members[i:i + per_row] for i in range(0, len(members), per_row)]
            # row height = max room height in that row (band alignment)
            cur_y = 0.0
            block_w = 0.0
            placed = []
            for ri, row in enumerate(rows):
                row_h = max(r["h"] for r in row)
                cur_x = 0.0
                for r in row:
                    # band-align: snap room height up to row_h if within area_tol,
                    # adjusting width to hold area; else leave a gap (keep exact).
                    target_h = row_h
                    new_w = r["a_model"] / target_h
                    # aspect clamp 1:2..2:1
                    asp = max(new_w, target_h) / max(1e-9, min(new_w, target_h))
                    dev = abs((new_w * target_h) - r["a_model"]) / r["a_model"]
                    if asp <= 2.0 and dev <= area_tol:
                        r["w"], r["h"] = new_w, target_h
                    # else keep exact w,h (small vertical gap remains -> allowed)
                    r["px"] = cur_x
                    r["py"] = cur_y - r["h"]
                    cur_x += r["w"] + room_gap
                    placed.append(r)
                block_w = max(block_w, cur_x - room_gap)
                cur_y -= row_h + channel_gap  # reserve BRANCH channel between rows
            block_h = -cur_y - channel_gap + channel_gap  # net height
            block_h = abs(cur_y) - channel_gap if len(rows) > 0 else 0.0
            cluster_blocks.append(dict(cid=cid, rooms=placed,
                                       w=block_w, h=abs(cur_y)))

        # ---- place clusters in a coarse grid, reserving TRUNK channels ----
        n_blocks = len(cluster_blocks)
        gcols = max(1, int(math.ceil(math.sqrt(n_blocks))))
        # column widths / row heights for the cluster grid
        col_w = [0.0] * gcols
        grow = int(math.ceil(n_blocks / float(gcols)))
        row_h = [0.0] * grow
        for bi, blk in enumerate(cluster_blocks):
            gc = bi % gcols
            gr = bi // gcols
            col_w[gc] = max(col_w[gc], blk["w"])
            row_h[gr] = max(row_h[gr], blk["h"])

        # cumulative offsets with channel_gap*? -> use a wider trunk gap
        trunk_gap = channel_gap * 1.5
        col_x = [0.0] * gcols
        for c in range(1, gcols):
            col_x[c] = col_x[c - 1] + col_w[c - 1] + trunk_gap
        row_y = [0.0] * grow
        for rr2 in range(1, grow):
            row_y[rr2] = row_y[rr2 - 1] - (row_h[rr2 - 1] + trunk_gap)

        # floor world offset (running accumulator)
        ox = floor_run if spread_axis == 0 else 0.0
        oy = floor_run if spread_axis == 1 else 0.0
        z = 0.0
        path_out = GH_Path(rank)

        floor_minx = 1e18
        floor_miny = 1e18
        floor_maxx = -1e18
        floor_maxy = -1e18

        for bi, blk in enumerate(cluster_blocks):
            gc = bi % gcols
            gr = bi // gcols
            bx = ox + col_x[gc]
            by = oy + row_y[gr]

            # cluster bbox accumulation
            cminx = 1e18
            cminy = 1e18
            cmaxx = -1e18
            cmaxy = -1e18

            for r in blk["rooms"]:
                x0 = bx + r["px"]
                y0 = by + r["py"]
                w = r["w"]
                h = r["h"]
                plane = rg.Plane(rg.Point3d(x0, y0, z), rg.Vector3d.ZAxis)
                rect = rg.Rectangle3d(plane, w, h)
                rooms.Add(rect.ToNurbsCurve(), path_out)
                room_lbl.Add(r["label"], path_out)
                room_uid.Add(r["uid"], path_out)
                room_floor.Add(int(fl_no), path_out)
                room_dept.Add(r["dept"], path_out)
                room_color.Add(r["color"], path_out)
                cluster_id.Add(r["cid"], path_out)

                # area deviation
                dev = ((w * h) - r["a_model"]) / r["a_model"] * 100.0
                dev_pct.Add(dev, path_out)
                if abs(dev) > area_tol * 100.0 + 1e-6:
                    total_over += 1

                # access_edge: face toward nearest reserved channel.
                # rooms in a cluster open onto the branch channel below/above;
                # simplest robust rule -> the room's -Y edge (row channel side),
                # unless it's the top row, then +Y. Refined later in Part 2.
                cx = x0 + w * 0.5
                cy = y0 + h * 0.5
                # distance to block edges
                dl = (x0) - bx
                dr = (bx + blk["w"]) - (x0 + w)
                db = (y0) - (by - blk["h"])
                dtp = (by) - (y0 + h)
                dists = [(dr, 0), (dtp, 1), (dl, 2), (db, 3)]
                dists.sort(key=lambda t: t[0])
                access_edge.Add(dists[0][1], path_out)

                total_rooms += 1
                cminx = min(cminx, x0); cminy = min(cminy, y0)
                cmaxx = max(cmaxx, x0 + w); cmaxy = max(cmaxy, y0 + h)

            # cluster rect
            if cmaxx > cminx:
                plane = rg.Plane(rg.Point3d(cminx, cminy, z), rg.Vector3d.ZAxis)
                cluster_rect.Add(rg.Rectangle3d(plane, cmaxx - cminx, cmaxy - cminy).ToNurbsCurve(), path_out)
                floor_minx = min(floor_minx, cminx); floor_miny = min(floor_miny, cminy)
                floor_maxx = max(floor_maxx, cmaxx); floor_maxy = max(floor_maxy, cmaxy)

        # floor envelope
        if floor_maxx > floor_minx:
            plane = rg.Plane(rg.Point3d(floor_minx, floor_miny, z), rg.Vector3d.ZAxis)
            env_rect.Add(rg.Rectangle3d(plane, floor_maxx - floor_minx, floor_maxy - floor_miny).ToNurbsCurve(), path_out)
            # echo the plate at this floor's origin so the fit is visible in Rhino
            pp = rg.Plane(rg.Point3d(ox, oy, z), rg.Vector3d.ZAxis)
            plate_rect.Add(rg.Rectangle3d(pp, plate_bw, plate_bh).ToNurbsCurve(), path_out)
            # BBOX FIT CHECK (Stage 3 reports; Stage 4 will reflow to fit)
            ew = floor_maxx - floor_minx
            eh = floor_maxy - floor_miny
            fits = (ew <= plate_bw + 1e-6) and (eh <= plate_bh + 1e-6)
            _fit_report.append(
                "  Floor {}: {:.0f}x{:.0f} vs plate {:.0f}x{:.0f} {}  {}".format(
                    fl_no, ew / lin, eh / lin, plate_bw / lin, plate_bh / lin,
                    unit_system, "fits" if fits else "OVERFLOWS BBOX"))
            # advance floor_run by this floor's extent along spread_axis
            extent = (floor_maxx - floor_minx) if spread_axis == 0 else (floor_maxy - floor_miny)
            floor_run += extent + floor_gap

        fill = floor_used.get(fl_no, 0.0) / capacity * 100.0 if capacity else 0.0
        lines.append("  Floor {} (branch {{{}}}) : {} rooms, {} clusters, {:.0f}% of capacity".format(
            fl_no, rank, len(ctr_list), n_cl, fill))

    depts = depts_in if depts_in else sorted(d for d in dept_set if d is not None)

    _prog_sf = sum(area_by_uid.get(u, 0.0) for u in g_uid)
    header = ["Component 03 | Cluster Tiler + Floor Assignment"]
    if n_unplaced_rooms:
        header.append("*** WARNING: {} of {} ROOMS NOT PLACED ({} chains). "
                      "Raise max_floors (now {}) or enlarge the plate. ***".format(
                          n_unplaced_rooms, N, len(unplaced), max_floors))
    header += ["Total rooms placed : {} of {}".format(total_rooms, N),
              "Unit system: {}   model-units/input: {:.4f}".format(unit_system, lin),
              "Plate ({}): {:.0f}x{:.0f} {}  area {:,.0f}  x eff {:.0%} -> capacity {:,.0f} {}2/floor".format(
                  plate_src, plate_bw / lin, plate_bh / lin, unit_system,
                  plate_area / (lin * lin), floor_efficiency,
                  capacity / (lin * lin), unit_system),
              "Program {:,.0f} {}2 over {} chains -> {} floors (max_floors={})".format(
                  _prog_sf, unit_system, len(grp), len(floors_used), max_floors),
              "Chains: {} atomic groups from {} rooms ({} multi-room shared chains)".format(
                  len(grp), N, sum(1 for g in grp if g["n"] > 1)),
              "Weights  w_prox={}  w_adj={}  w_dept={}  (n_clusters max={})".format(w_prox, w_adj, w_dept, n_clusters),
              "area_tol={:.0%}  channel_gap={} {}  rooms over tol: {}".format(
                  area_tol, _get("channel_gap", 7.0), unit_system, total_over)]
    if _fit_report:
        header.append("BBOX FIT (Stage 3 reports only; tiler does not reflow yet):")
        header += _fit_report
    if overage_log:
        header.append("OVERAGE - chain larger than one plate ({}):".format(len(overage_log)))
        for o in overage_log:
            header.append("  " + o)
    if overflow_log:
        header.append("OVERFLOW / preference not honoured ({}):".format(len(overflow_log)))
        for o in overflow_log[:12]:
            header.append("  " + o)
        if len(overflow_log) > 12:
            header.append("  ... {} more".format(len(overflow_log) - 12))
    if pref_conflicts:
        header.append("NOTE chain members with conflicting floor_pref ({}):".format(len(pref_conflicts)))
        for c in pref_conflicts[:6]:
            header.append("  " + c)
    return "\n".join(header + lines)


_do_run = True if ('run' not in globals() or run is None) else bool(run)
if _do_run:
    info = _build()
else:
    info = "Idle: set run=True to tile."
