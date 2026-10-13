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
#   depts      : int     | List | passthrough
#   info       : str     | Item | diagnostics
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
depts = []
info = ""


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

    # iterate floors in branch order
    for (path, ctr_list) in centers_b:
        key = str(path)
        rank = path.Indices[0] if path.Indices else 0
        n = len(ctr_list)
        if n == 0:
            continue

        rr = radii_b.get(key, [1.0] * n)
        lb = labels_b.get(key, [""] * n)
        cl = colors_b.get(key, [System.Drawing.Color.Gray] * n)
        uu = uid_b.get(key, ["?%d" % i for i in range(n)])
        dd = dept_b.get(key, [-1] * n)

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
            # advance floor_run by this floor's extent along spread_axis
            extent = (floor_maxx - floor_minx) if spread_axis == 0 else (floor_maxy - floor_miny)
            floor_run += extent + floor_gap

        lines.append("  Floor branch {{{}}} : {} rooms, {} clusters".format(rank, len(ctr_list), n_cl))

    depts = depts_in if depts_in else sorted(d for d in dept_set if d is not None)

    header = ["Component 03 | Cluster Tiler",
              "Total rooms placed : {}".format(total_rooms),
              "Unit system: {}   model-units/input: {:.4f}".format(unit_system, lin),
              "Weights  w_prox={}  w_adj={}  w_dept={}  (n_clusters max={})".format(w_prox, w_adj, w_dept, n_clusters),
              "area_tol={:.0%}  channel_gap={} {}  rooms over tol: {}".format(
                  area_tol, _get("channel_gap", 7.0), unit_system, total_over)]
    return "\n".join(header + lines)


_do_run = True if ('run' not in globals() or run is None) else bool(run)
if _do_run:
    info = _build()
else:
    info = "Idle: set run=True to tile."
