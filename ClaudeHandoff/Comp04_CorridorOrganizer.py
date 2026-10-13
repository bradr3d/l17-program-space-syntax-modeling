#! python3
# ==============================================================================
# COMPONENT 04  |  CORRIDOR ORGANIZER  (double-loaded clusters + branch/trunk)
# ------------------------------------------------------------------------------
# Rhino 8 / Grasshopper CPython script component.
# Takes the clustered rooms from Component 03 and RE-LAYS-OUT each cluster as a
# double-loaded arrangement: two rows of rooms facing a central BRANCH channel,
# with the corridor-side (access) edge of every room pinned to ONE perfectly
# flat line. Rooms keep EXACT area; width is fixed per room, so the FAR edge
# falls wherever area puts it (accepted stagger, maximum clean corridor contact).
#
# Clusters are then PLACED BY ADJACENCY: the strongest-connected cluster pairs
# sit with facing edges toward each other across a reserved TRUNK channel, whose
# centerline connects the two branch corridors. Weaker adjacencies route the
# long way (documented, not forced).
#
# This component does NOT reshape rooms or re-cluster. It reads Comp03's cluster
# assignment + each room's exact area and rebuilds POSITIONS only. It emits
# reserved channel rectangles + corridor CENTERLINES (curves), not corridor
# width geometry -- the stem/width pass is Part 2.
#
# ------------------------------------------------------------------------------
# PIPELINE CONTRACT
#   01 Reader -> areas, uids_flat, adj_source, adj_target (flat lists)
#   03 Tiler  -> grafted per floor: rooms, room_uid, room_dept, room_color,
#                cluster_id, cluster_rect, env_rect
#   04 (this) -> grafted per floor: re-laid double-loaded rooms + corridors
#
# ------------------------------------------------------------------------------
# INPUTS
#   room_uid    : str   | Tree | from 03 (branch = floor rank)
#   room_dept   : int   | Tree | from 03
#   room_color  : Colour| Tree | from 03
#   room_lbl    : str   | Tree | from 03 label per room ("1.1 Classroom - K (1/4)").
#                                Looked up by uid and echoed on output.
#                                NOTE: wire Comp03's room_lbl into the INPUT named
#                                'lbl_in' (the output tree is also called room_lbl;
#                                the names must differ, as with shared/shared_out
#                                in Comp01). If absent, output falls back to uid.
#   cluster_id  : int   | Tree | from 03 (cluster index within floor)
#
#   uids_flat   : str   | List | from 01 'uid'  (index-aligned to areas)
#   areas       : float | List | from 01 'area' (exact SF per room)
#   adj_source  : str   | List | from 01 (edge tails, uid)
#   adj_target  : str   | List | from 01 (edge heads, uid)
#   shared      : bool  | List | from 01 'shared_out' (index-aligned to uids_flat)
#                                Rooms flagged Shared Access are GLUED next to
#                                their adjacency parent in "bubble" split so they
#                                share a wall (Comp06 places a direct door there).
#
#   sol_centers : Point3d| Tree | from 02 'centers' (solver bubble positions)
#   sol_uid     : str   | Tree | from 02 'uid_out'  (branch-aligned to centers)
#
#   unit_system : str   | Item | "feet" or "meters" (areas' units)
#   split_mode  : str   | Item | "bubble" or "even" (row split rule). Default "bubble"
#   room_depth  : float | Item | target/cap room depth (perp to corridor). Default 28
#   max_aspect  : float | Item | max room aspect (long:short) before depth grows. Default 2.5
#   branch_gap  : float | Item | reserved BRANCH channel width. Default 6.0
#   trunk_gap   : float | Item | reserved TRUNK channel width. Default 8.0
#   room_gap    : float | Item | gap between rooms along a row. Default 0.5
#   branch_gap_v: float | Item | vertical gap between stacked branches. Default 8.0
#   envelope_aspect : float | Item | target floor width:height for shelf wrap. Default 1.4
#   shelf_gap   : float | Item | vertical gap between shelves. Default 10.0
#   big_room_frac : float | Item | room-width fraction of run to extract. Default 0.6
#   max_corridor_len : float | Item | max branch run before a break split. Default 75
#   break_gap   : float | Item | gap inserted between room groups at a break. Default 6.0
#   snap_dist   : float | Item | merge verticals within this dist onto gap channels. Default 12.0
#   spread_axis : int   | Item | 0=X, 1=Y floor offset direction. Default 0
#   floor_gap   : float | Item | gap between floor blocks. Default 30.0
#   run         : bool  | Item | solve toggle
#
# ------------------------------------------------------------------------------
# OUTPUTS (GRAFTED PER FLOOR, branch = floor rank)
#   rooms       : Rectangle3d(Curve) | Tree | re-laid double-loaded rooms
#   room_lbl    : str   | Tree | label per room ("1.1 Classroom - K (1/4)", from 03)
#   room_uid_out: str   | Tree | uid per room (name-based lookups downstream)
#   room_dept_out: int  | Tree | dept per room
#   room_color_out: Colour| Tree | color per room
#   cluster_id_out: int | Tree | cluster per room
#   access_edge : int   | Tree | 0=+X 1=+Y 2=-X 3=-Y face onto branch channel
#   branch_ctr  : Curve | Tree | branch corridor centerline per cluster
#   branch_rect : Rectangle3d(Curve) | Tree | reserved branch channel rect
#   trunk_ctr   : Curve | Tree | trunk centerlines (shelf spine + gap-channel
#                                verticals + half-span break corridors)
#   trunk_weight: int   | Tree | rooms served per trunk_ctr segment (for widening)
#   cluster_rect_out : Rectangle3d(Curve) | Tree | re-laid cluster bbox
#   env_rect    : Rectangle3d(Curve) | Tree | floor envelope
#   depts       : int   | List | passthrough
#   info        : str   | Item | diagnostics (incl. weak adjacencies routed long)
#
# ------------------------------------------------------------------------------
# CONVENTIONS / GOTCHAS
#   - "#! python3" required. Rhino.Geometry throughout.
#   - Tree inputs on Tree Access; flat inputs on List Access; scalars Item.
#   - ACCESS EDGE IS PINNED FLAT: all rooms in a row share the same corridor-side
#     Y (top row) / Y (bottom row). Width fixed; depth = area/width; far edge
#     staggers. No tolerance on the access edge.
#   - Name-based lookups by uid. int(math.ceil()); isinstance() for types.
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


# ---- outputs ---------------------------------------------------------------
rooms = DataTree[object]()
room_lbl = DataTree[object]()
room_uid_out = DataTree[object]()
room_dept_out = DataTree[object]()
room_color_out = DataTree[object]()
cluster_id_out = DataTree[object]()
access_edge = DataTree[object]()
branch_ctr = DataTree[object]()
branch_rect = DataTree[object]()
trunk_ctr = DataTree[object]()
trunk_weight = DataTree[object]()
cluster_rect_out = DataTree[object]()
env_rect = DataTree[object]()
depts = []
info = ""


def _build():
    global depts
    lines = []

    unit_system = _get("unit_system", "feet")
    lin = _unit_scale(unit_system)
    split_mode = str(_get("split_mode", "bubble")).strip().lower()
    room_depth = float(_get("room_depth", 28.0)) * lin
    max_aspect = float(_get("max_aspect", 2.5))
    branch_gap = float(_get("branch_gap", 6.0)) * lin
    trunk_gap = float(_get("trunk_gap", 8.0)) * lin
    room_gap = float(_get("room_gap", 0.5)) * lin
    branch_gap_v = float(_get("branch_gap_v", 8.0)) * lin
    envelope_aspect = float(_get("envelope_aspect", 1.0))
    shelf_gap = float(_get("shelf_gap", 10.0)) * lin
    big_room_frac = float(_get("big_room_frac", 0.6))
    max_corridor_len = float(_get("max_corridor_len", 75.0)) * lin
    break_gap = float(_get("break_gap", 6.0)) * lin
    snap_dist = float(_get("snap_dist", 12.0)) * lin
    spread_axis = int(_get("spread_axis", 0))
    floor_gap = float(_get("floor_gap", 30.0)) * lin

    # ---- flat lookups ------------------------------------------------------
    uids_flat = list(_get("uids_flat", []))
    areas_flat = list(_get("areas", []))
    area_by_uid = {}
    for i in range(min(len(uids_flat), len(areas_flat))):
        area_by_uid[uids_flat[i]] = float(areas_flat[i])

    asrc = list(_get("adj_source", []))
    atgt = list(_get("adj_target", []))

    # ---- shared-access flag by uid (glue child rooms to their parent) ------
    shared_flat = list(_get("shared", []))
    shared_by_uid = {}
    for i in range(min(len(uids_flat), len(shared_flat))):
        shared_by_uid[uids_flat[i]] = bool(shared_flat[i])

    # room-level adjacency parent: uid -> its Conn.Primary target uid.
    # (Comp01 wires directed source->target; source PULLS toward target.)
    parent_of = {}
    for s, t in zip(asrc, atgt):
        parent_of[s] = t

    # ---- solver bubble positions by uid (for cluster footprint width) -----
    sol_pos = {}   # uid -> (x, y) from Comp02 centers
    sc_b = _branches(_get("sol_centers", None))
    su_b = dict((str(p), v) for p, v in _branches(_get("sol_uid", None)))
    for (p, pts) in sc_b:
        us = su_b.get(str(p), [])
        for i in range(min(len(pts), len(us))):
            sol_pos[us[i]] = (pts[i].X, pts[i].Y)

    # ---- grafted inputs from Comp03 ---------------------------------------
    uid_b = _branches(_get("room_uid", None))
    if not uid_b:
        return "Waiting: no 'room_uid' tree from Component 03."
    dept_b = dict((str(p), v) for p, v in _branches(_get("room_dept", None)))
    color_b = dict((str(p), v) for p, v in _branches(_get("room_color", None)))
    cid_b = dict((str(p), v) for p, v in _branches(_get("cluster_id", None)))

    # label per room, keyed by uid (from Comp03). Falls back to the uid at emit
    # time if the input is absent or a uid is missing. Robust to the input's
    # Access setting: if 'lbl_in' comes in as a Tree we pair labels to uids per
    # branch; if it arrives as a flat List (Access = List) we pair against the
    # flattened uid order. Either way the label is joined to the room by uid.
    label_by_uid = {}
    lbl_raw = _get("lbl_in", None)
    lbl_b = _branches(lbl_raw)
    _uid_by_key = dict((str(p), v) for p, v in uid_b)
    if lbl_b:
        # Tree input: pair per branch, but tolerate branch-key mismatch by
        # falling back to flattened order when a branch key has no uid match.
        matched_any = False
        for (p, lbls) in lbl_b:
            us = _uid_by_key.get(str(p), [])
            if us:
                matched_any = True
                for i in range(min(len(us), len(lbls))):
                    label_by_uid[us[i]] = lbls[i]
        if not matched_any:
            # branch keys didn't line up (e.g. depth mismatch {0;0} vs {0});
            # flatten both and pair by order.
            flat_uids = [u for _p, us in uid_b for u in us]
            flat_lbls = [l for _p, ls in lbl_b for l in ls]
            for i in range(min(len(flat_uids), len(flat_lbls))):
                label_by_uid[flat_uids[i]] = flat_lbls[i]
    elif lbl_raw is not None:
        # List/Item input: treat as a flat sequence aligned to flattened uids.
        try:
            flat_lbls = list(lbl_raw)
        except TypeError:
            flat_lbls = [lbl_raw]
        flat_uids = [u for _p, us in uid_b for u in us]
        for i in range(min(len(flat_uids), len(flat_lbls))):
            label_by_uid[flat_uids[i]] = flat_lbls[i]

    floor_run = 0.0
    total_rooms = 0
    weak_routed = []
    dept_set = set()
    _eff_report = []

    for (path, uid_list) in uid_b:
        key = str(path)
        rank = path.Indices[0] if path.Indices else 0
        n = len(uid_list)
        if n == 0:
            continue

        dd = dept_b.get(key, [-1] * n)
        cc = color_b.get(key, [System.Drawing.Color.Gray] * n)
        ci = cid_b.get(key, [0] * n)

        # room records for THIS floor
        recs = []
        for i in range(n):
            u = uid_list[i]
            a_in = area_by_uid.get(u, 100.0)
            recs.append(dict(uid=u, dept=dd[i] if i < len(dd) else -1,
                             color=cc[i] if i < len(cc) else System.Drawing.Color.Gray,
                             cid=int(ci[i]) if i < len(ci) else 0,
                             area_model=a_in * (lin * lin)))
            dept_set.add(recs[-1]["dept"])

        clusters = sorted(set(r["cid"] for r in recs))

        # ---- cluster adjacency graph (for placement) ----------------------
        cid_of_uid = {r["uid"]: r["cid"] for r in recs}
        pair_w = {}
        for s, t in zip(asrc, atgt):
            if s in cid_of_uid and t in cid_of_uid:
                a, b = cid_of_uid[s], cid_of_uid[t]
                if a == b:
                    continue
                kk = (min(a, b), max(a, b))
                pair_w[kk] = pair_w.get(kk, 0) + 1

        # ---- lay out each cluster locally (double-loaded, WRAPPING) -------
        # Each room targets room_depth; area preserved by WIDENING along the
        # corridor up to max_aspect, then (only if still too big) deepening.
        # Rows wrap into stacked branches when a run exceeds the cluster's
        # solver footprint width (faithful to Comp02 bubble extent).
        cluster_layout = {}
        for cid in clusters:
            members = [r for r in recs if r["cid"] == cid]
            m = len(members)
            if m == 0:
                continue

            # --- solver footprint width for this cluster (wrap target) ------
            xs = [sol_pos[r["uid"]][0] for r in members if r["uid"] in sol_pos]
            if len(xs) >= 2:
                foot_w = (max(xs) - min(xs))
            else:
                foot_w = 0.0
            # convert solver units to model units (solver already model units);
            # ensure a sane minimum so tiny clusters still hold a few rooms
            min_run = 3.0 * room_depth
            max_run = max(min_run, foot_w if foot_w > 0 else min_run)

            # --- per-room shaping: width & depth from area, depth-capped ----
            def _shape(area_model):
                """Return (w, depth) with access edge flat. Target depth =
                room_depth; widen (up to max_aspect) to hold area; if the
                room still doesn't fit, let depth grow beyond the cap."""
                depth = room_depth
                w = area_model / depth
                # aspect = long:short
                asp = max(w, depth) / max(1e-9, min(w, depth))
                if asp <= max_aspect:
                    return w, depth
                # too skinny/wide at target depth -> clamp aspect, grow depth
                # keep width at max_aspect * depth, solve depth for exact area
                # area = w * depth, w = max_aspect * depth  (wide-shallow case)
                if w > depth:
                    # wide room: w = max_aspect * depth -> depth = sqrt(area/max_aspect)
                    depth = math.sqrt(area_model / max_aspect)
                    w = area_model / depth
                else:
                    # tall room: depth = max_aspect * w -> w = sqrt(area/max_aspect)
                    w = math.sqrt(area_model / max_aspect)
                    depth = area_model / w
                return w, depth

            # --- ORDER members ------------------------------------------------
            # "bubble" (default): honour the solver's bubble layout AND keep each
            # adjacency chain contiguous, so a shared-access child (restroom,
            # vestibule) sits immediately beside its parent classroom and they
            # share a wall (Comp06 puts the direct door there). Without this the
            # rooms scatter and shared doors have no adjacent parent to open into.
            # "even": legacy width-balanced packing (kept for comparison).
            def _sol_x(rec):
                p = sol_pos.get(rec["uid"])
                return p[0] if p else 0.0

            member_uids = set(r["uid"] for r in members)
            rec_by_uid = {r["uid"]: r for r in members}

            if split_mode == "even":
                # legacy: solver order, then rows width-balanced (scatters chains)
                ordered = sorted(members, key=_sol_x)
            else:
                # bubble: build adjacency CHAINS. A chain is a parent followed by
                # every shared-access descendant that points (transitively) back
                # into this cluster. Chains are ordered left->right by the parent's
                # solver x so the branch still tracks the bubble diagram.
                #
                # child -> parent within this cluster (only for shared rooms; a
                # non-shared room stands alone even if it has a Conn.Primary, since
                # only shared rooms need a direct wall to their parent).
                child_to_parent = {}
                for r in members:
                    u = r["uid"]
                    if shared_by_uid.get(u, False):
                        par = parent_of.get(u)
                        if par in member_uids:
                            child_to_parent[u] = par
                # parent -> ordered list of its shared children
                kids_of = {}
                for child, par in child_to_parent.items():
                    kids_of.setdefault(par, []).append(child)
                for par in kids_of:
                    kids_of[par].sort(key=lambda u: _sol_x(rec_by_uid[u]))

                placed_uids = set()
                ordered = []

                def _emit_chain(u):
                    # depth-first: parent, then its shared children (each of which
                    # may itself have shared grandchildren -> vestibule off restroom)
                    if u in placed_uids or u not in rec_by_uid:
                        return
                    placed_uids.add(u)
                    ordered.append(rec_by_uid[u])
                    for kid in kids_of.get(u, []):
                        _emit_chain(kid)

                # chain roots = members that are NOT a shared child of another
                # member, taken left->right by solver x. Everything hangs off them.
                roots = [r for r in members if r["uid"] not in child_to_parent]
                roots.sort(key=_sol_x)
                for r in roots:
                    _emit_chain(r["uid"])
                # safety: sweep any member missed (cycles / orphans)
                for r in sorted(members, key=_sol_x):
                    _emit_chain(r["uid"])

            # pre-shape all rooms (order preserved)
            shaped = []
            for r in ordered:
                w, depth = _shape(r["area_model"])
                shaped.append(dict(rec=r, w=w, depth=depth,
                                   shared=shared_by_uid.get(r["uid"], False),
                                   parent=parent_of.get(r["uid"])))

            # --- big-room extraction: a room whose width alone exceeds a large
            # fraction of max_run dominates its branch. Pull these OUT into their
            # own single-room branches placed at the FAR edge (away from trunk),
            # so they stop walling off the smaller rooms. A shared-access room is
            # NEVER extracted -- it must stay beside its parent.
            big_thresh = big_room_frac * max_run
            big_rooms = [sr for sr in shaped if sr["w"] >= big_thresh and not sr["shared"]]
            normal = [sr for sr in shaped if not (sr["w"] >= big_thresh and not sr["shared"])]

            # --- pack rooms into rows, wrapping at max_run --------------------
            # KEEP CHAINS UNBROKEN: a parent and its shared children are an atomic
            # group; if the group won't fit the current row, wrap the WHOLE group
            # to the next row rather than splitting parent from child.
            groups = []          # list of lists of shaped-rooms (each an atom)
            k = 0
            while k < len(normal):
                grp = [normal[k]]
                # absorb immediately-following shared children of this room
                base_uid = normal[k]["rec"]["uid"]
                j = k + 1
                while j < len(normal) and normal[j]["shared"] and \
                        normal[j]["parent"] in (g["rec"]["uid"] for g in grp):
                    grp.append(normal[j])
                    j += 1
                groups.append(grp)
                k = j

            if split_mode == "even":
                # legacy behaviour: flat wrap, no atomic groups
                groups = [[sr] for sr in normal]

            rows = []
            cur = []
            cur_w = 0.0
            for grp in groups:
                gw = sum(g["w"] for g in grp) + room_gap * (len(grp) - 1)
                add_w = gw + (room_gap if cur else 0.0)
                if cur and (cur_w + add_w) > max_run:
                    rows.append(cur)
                    cur = list(grp)
                    cur_w = gw
                else:
                    cur.extend(grp)
                    cur_w += add_w
            if cur:
                rows.append(cur)

            # --- pair rows into branches --------------------------------------
            branches = []
            if split_mode == "even":
                # legacy: width-balance the pooled rows (scatters adjacency)
                for i in range(0, len(rows), 2):
                    pool = list(rows[i])
                    if i + 1 < len(rows):
                        pool += list(rows[i + 1])
                    pool.sort(key=lambda sr: -sr["w"])
                    top_row, bot_row = [], []
                    tw, bw = 0.0, 0.0
                    for sr in pool:
                        if tw <= bw:
                            top_row.append(sr); tw += sr["w"] + room_gap
                        else:
                            bot_row.append(sr); bw += sr["w"] + room_gap
                    branches.append(dict(top_row=top_row, bot_row=bot_row,
                                         count=len(top_row) + len(bot_row)))
            else:
                # bubble: consecutive rows become the two sides of a branch, IN
                # ORDER (no width sort, no re-deal). Adjacency chains stay intact
                # and neighbours stay neighbours; a shared child ends up directly
                # beside its parent in the same row.
                for i in range(0, len(rows), 2):
                    top_row = list(rows[i])
                    bot_row = list(rows[i + 1]) if i + 1 < len(rows) else []
                    branches.append(dict(top_row=top_row, bot_row=bot_row,
                                         count=len(top_row) + len(bot_row)))

            # --- ORDER branches by room count: MOST rooms nearest the trunk
            # (branches are stacked away from the trunk, so the first in this
            # list sits closest to trunk). Big single rooms go LAST (far edge).
            branches.sort(key=lambda b: -b["count"])
            for sr in big_rooms:
                branches.append(dict(top_row=[sr], bot_row=[], count=1, big=True))

            # --- assign local coordinates, stacking branches vertically ----
            # Breaks are decided ONCE per branch from the longer row's geometry,
            # then applied identically to both rows so the cross-corridor lines up.
            # A break inserts break_gap; the run length resets AT each break so no
            # segment exceeds max_corridor_len.
            # Each row breaks at ITS OWN room boundaries. The break corridor spans
            # only that row's half of the branch (centerline to that row's far
            # edge), so it never reaches into the other row's rooms. Rows meet at
            # the branch centerline (already a corridor) -> network stays connected.
            def _lay_row(row, y_access, grow_up, do_breaks):
                x = 0.0
                placed = []
                bxs = []
                run_start = 0.0
                for i, sr in enumerate(row):
                    w = sr["w"]; depth = sr["depth"]
                    if do_breaks and (x - run_start) + w > max_corridor_len and x > 0.0:
                        bxs.append(x + break_gap * 0.5)
                        x += break_gap
                        run_start = x
                    y0 = y_access if grow_up else y_access - depth
                    placed.append(dict(rec=sr["rec"], x=x, y=y0, w=w, h=depth,
                                       access_up=grow_up))
                    x += w + room_gap
                return placed, (x - room_gap if row else 0.0), bxs

            laid_branches = []
            cur_y = 0.0    # top of the current branch stack (descending)
            cluster_w = 0.0
            for br in branches:
                tmp_top, top_w, _ = _lay_row(br["top_row"], 0.0, True, False)
                tmp_bot, bot_w, _ = _lay_row(br["bot_row"], 0.0, False, False)
                top_depth = max((p["h"] for p in tmp_top), default=0.0)
                bot_depth = max((p["h"] for p in tmp_bot), default=0.0)

                # --- SINGLE-LOADED: face the corridor TOWARD the trunk ---------
                # A single-loaded branch has rooms on only one side. Place that
                # row on the branch's trunk-FACING side so its access corridor
                # points at the trunk (shortening the connection), not the edge.
                # The cluster's dense edge (loc_top) pins to the trunk, so the
                # trunk-facing side of a branch is its UPPER side (top_row) for
                # non-mirrored (bottom-band) and lower for mirrored (top-band).
                # We normalize to top_row here; the emit's mirror_y then points it
                # at the trunk for whichever band the cluster lands in.
                one_sided = (len(br["top_row"]) == 0) != (len(br["bot_row"]) == 0)
                if one_sided:
                    # consolidate the single row into top_row (trunk-facing after
                    # the cluster's dense-to-trunk orientation)
                    solo = br["top_row"] if br["top_row"] else br["bot_row"]
                    br["top_row"], br["bot_row"] = solo, []
                    tmp_top, top_w, _ = _lay_row(br["top_row"], 0.0, True, False)
                    tmp_bot, bot_w, _ = _lay_row(br["bot_row"], 0.0, False, False)
                    top_depth = max((p["h"] for p in tmp_top), default=0.0)
                    bot_depth = max((p["h"] for p in tmp_bot), default=0.0)
                    br["single_loaded"] = True

                bc_y = cur_y - top_depth - branch_gap * 0.5
                top_placed, top_w2, top_bxs = _lay_row(br["top_row"], bc_y + branch_gap * 0.5, True, True)
                bot_placed, bot_w2, bot_bxs = _lay_row(br["bot_row"], bc_y - branch_gap * 0.5, False, True)
                # breaks stay SIDE-TAGGED: top breaks span centerline->top edge,
                # bottom breaks span centerline->bottom edge (half-span each).
                top_break_xs = list(top_bxs)
                bot_break_xs = list(bot_bxs)

                bw = max(top_w2, bot_w2, 1.0)
                cluster_w = max(cluster_w, bw)
                laid_branches.append(dict(
                    top=top_placed, bot=bot_placed, w=bw,
                    top_break_xs=top_break_xs, bot_break_xs=bot_break_xs,
                    top_depth=top_depth, bot_depth=bot_depth, bc_y=bc_y,
                    single_loaded=bool(br.get("single_loaded")),
                    big=bool(br.get("big"))))

                # advance stack downward: this branch total height + vertical gap
                branch_total = top_depth + branch_gap + bot_depth
                cur_y = bc_y - bot_depth - branch_gap * 0.5 - branch_gap_v

            cluster_h = abs(cur_y) if laid_branches else 0.0
            # local vertical extent of the cluster's rooms (for shelf band align):
            # top row of first branch sits highest, bottom row of last lowest.
            loc_top = -1e18
            loc_bot = 1e18
            for lb in laid_branches:
                for p in lb["top"] + lb["bot"]:
                    loc_top = max(loc_top, p["y"] + p["h"])
                    loc_bot = min(loc_bot, p["y"])
            if loc_top < loc_bot:
                loc_top, loc_bot = 0.0, 0.0
            # DENSE end: branches[0] is the most-populated (sorted). Its rooms
            # define the edge that should sit AGAINST the trunk. branch[0] is at
            # the TOP of the local stack, so its far (upper) edge is the dense
            # edge = loc_top. We record dense_at_top so emit pins the dense edge
            # to the trunk regardless of band.
            cluster_layout[cid] = dict(branches=laid_branches, w=cluster_w, h=cluster_h,
                                       loc_top=loc_top, loc_bot=loc_bot,
                                       dense_at_top=True)

        # ---- place clusters by adjacency (greedy strongest-first) ---------
        # order clusters: seed with the most-connected, then append neighbors
        order = []
        remaining = set(cluster_layout.keys())
        if remaining:
            # seed = cluster with highest total adjacency weight
            deg = {c: 0 for c in remaining}
            for (a, b), w in pair_w.items():
                if a in deg:
                    deg[a] += w
                if b in deg:
                    deg[b] += w
            seed = max(remaining, key=lambda c: deg.get(c, 0))
            order.append(seed)
            remaining.discard(seed)
            while remaining:
                # pick the remaining cluster with strongest tie to placed set
                best_c, best_w = None, -1
                for c in remaining:
                    tie = 0
                    for p in order:
                        kk = (min(c, p), max(c, p))
                        tie += pair_w.get(kk, 0)
                    if tie > best_w:
                        best_w, best_c = tie, c
                order.append(best_c)
                remaining.discard(best_c)
                if best_w == 0:
                    weak_routed.append("floor {} cluster {} (no direct adjacency to placed)".format(rank, best_c))

        # ---- SHELF PACKING (Option A: double-loaded shelves) --------------
        ox = floor_run if spread_axis == 0 else 0.0
        oy = floor_run if spread_axis == 1 else 0.0
        z = 0.0
        path_out = GH_Path(rank)

        floor_minx = 1e18; floor_miny = 1e18
        floor_maxx = -1e18; floor_maxy = -1e18

        # target envelope width. Clusters are chunky 2D blocks placed on
        # DOUBLE-LOADED shelves: each shelf's width is the longer of its two
        # bands (top/bottom alternate), so a shelf holding k clusters spans
        # ~ (sum_cluster_w / 2) not the full sum. Its height is the two tallest
        # band clusters stacked. We search shelf-count n for the envelope aspect
        # closest to target.
        sum_cluster_w = sum(cluster_layout[c]["w"] for c in order)
        sum_cluster_w += trunk_gap * max(0, len(order) - 1)
        max_cluster_h = max((cluster_layout[c]["h"] for c in order), default=1.0)
        widest = max((cluster_layout[c]["w"] for c in order), default=sum_cluster_w)
        n_clusters_here = max(1, len(order))
        best_n = 1
        best_err = 1e18
        for n_try in range(1, n_clusters_here + 1):
            # clusters per shelf ~ n_clusters/n_try, split across 2 bands
            per_shelf = n_clusters_here / float(n_try)
            # width of a shelf ~ (per_shelf/2) * avg_cluster_w
            avg_w = sum_cluster_w / n_clusters_here
            ew = max(widest, (per_shelf / 2.0) * avg_w)
            # height ~ n_try shelves, each ~2 band-heights + gaps
            eh = n_try * (2.0 * max_cluster_h + trunk_gap + shelf_gap)
            asp = ew / eh if eh else 1.0
            err = abs(asp - envelope_aspect)
            if err < best_err:
                best_err, best_n = err, n_try
        # env_w threshold: the packing loop tracks per-band width (top/bottom
        # fill independently via fill-shorter-band), so a shelf wraps when the
        # shorter band would exceed env_w. best_n is chosen to approach the
        # target aspect; the widest single cluster always fits.
        env_w = max(widest, sum_cluster_w / max(1, best_n))

        # assign clusters to shelves. Clusters go to whichever BAND (top/bottom)
        # is currently SHORTER (fills gaps instead of stranding clusters on the
        # end of one band). A shelf wraps when the shorter band would still
        # exceed env_w after adding.
        shelves = []
        cur_items = []
        band_w = [0.0, 0.0]   # [top, bottom] running widths
        for cid in order:
            cw = cluster_layout[cid]["w"]
            b = 0 if band_w[0] <= band_w[1] else 1   # fill the shorter band
            add_w = cw + (trunk_gap if band_w[b] > 0 else 0.0)
            if cur_items and (band_w[b] + add_w) > env_w:
                shelves.append(dict(items=cur_items,
                                    width=max(band_w[0], band_w[1])))
                cur_items = []
                band_w = [0.0, 0.0]
                b = 0
                add_w = cw
            cur_items.append((cid, b))
            band_w[b] += add_w
        if cur_items:
            shelves.append(dict(items=cur_items, width=max(band_w[0], band_w[1])))

        # place shelves stacking upward; each shelf has a central trunk line,
        # TOP-band clusters sit above it, BOTTOM-band clusters below it.
        # Vertical corridors (gap channels + half-span breaks) are COLLECTED
        # here per floor, then consolidated (snap to gaps + merge) and emitted
        # after all shelves -- so we get one clean vertical per gap, not doubles.
        _vcorr = []   # each: dict(x, y0, y1, weight, kind) kind: gap|break
        _gap_xs = []  # trunk_gap channel center x-positions (snap targets)
        _room_spans = []  # (x0, x1) of each room, for the perimeter test

        # --- WEIGHTED CENTROID X (room count x cluster position) --------------
        # Pre-estimate each cluster's center x from the band packing so rooms can
        # fill TOWARD the building center. Weight by room count. Computed from
        # cluster positions (fixed by packing) -> no fill/centroid circularity.
        def _cluster_room_count(cid):
            return sum(len(b["top"]) + len(b["bot"]) for b in cluster_layout[cid]["branches"])
        _cx_est = {}
        for shelf in shelves:
            for band in (0, 1):
                cx = ox
                for cid, bd in shelf["items"]:
                    if bd != band:
                        continue
                    lay = cluster_layout[cid]
                    _cx_est[cid] = cx + lay["w"] * 0.5
                    cx += lay["w"] + trunk_gap
        _tot_w = sum(_cluster_room_count(c) for c in order) or 1
        centroid_x = sum(_cx_est.get(c, ox) * _cluster_room_count(c) for c in order) / _tot_w

        shelf_y = oy
        for si, shelf in enumerate(shelves):
            # shelf band depths: tallest top-band cluster above, bottom below
            top_ids = [cid for cid, b in shelf["items"] if b == 0]
            bot_ids = [cid for cid, b in shelf["items"] if b == 1]
            top_depth = max((cluster_layout[c]["loc_top"] - cluster_layout[c]["loc_bot"]
                             for c in top_ids), default=0.0)
            bot_depth = max((cluster_layout[c]["loc_top"] - cluster_layout[c]["loc_bot"]
                             for c in bot_ids), default=0.0)

            trunk_y = shelf_y + bot_depth + trunk_gap * 0.5   # shelf trunk datum

            # lay a band with SUB-STACKING: clusters pack into columns. The
            # first cluster in a column sits against the trunk (dense branch
            # facing it); shorter following clusters STACK behind it (away from
            # trunk) to fill the band height before moving to a new column.
            # Adjacency order preserved along the column fill.
            def _place_band(ids, band, band_depth):
                cx = ox
                centers_this = []
                i = 0
                while i < len(ids):
                    # start a new column with cluster ids[i] against the trunk
                    col = [ids[i]]
                    col_w = cluster_layout[ids[i]]["w"]
                    used_depth = cluster_layout[ids[i]]["loc_top"] - cluster_layout[ids[i]]["loc_bot"]
                    j = i + 1
                    # stack following clusters behind if they fit the remaining depth
                    while j < len(ids):
                        ch = cluster_layout[ids[j]]["loc_top"] - cluster_layout[ids[j]]["loc_bot"]
                        if used_depth + shelf_gap + ch <= band_depth + 1e-6:
                            col.append(ids[j])
                            col_w = max(col_w, cluster_layout[ids[j]]["w"])
                            used_depth += shelf_gap + ch
                            j += 1
                        else:
                            break
                    # place the column: first cluster at trunk, rest stacked away
                    depth_cursor = 0.0
                    for ci, cid in enumerate(col):
                        lay = cluster_layout[cid]
                        ch = lay["loc_top"] - lay["loc_bot"]
                        bx = cx
                        # FILL TOWARD CENTER: a cluster LEFT of the centroid packs
                        # its rooms right-to-left (mirror_x) so they sit toward the
                        # building center; a cluster RIGHT of centroid fills left-
                        # to-right (no mirror). Rooms therefore always pack inward.
                        cl_center = bx + lay["w"] * 0.5
                        mx = cl_center < centroid_x
                        if band == 0:
                            trunk_edge = (trunk_y + trunk_gap * 0.5) + depth_cursor
                            by = trunk_edge + lay["loc_top"]
                            _emit_cluster(cid, bx, by, mirror_y=True, mirror_x=mx)
                        else:
                            trunk_edge = (trunk_y - trunk_gap * 0.5) - depth_cursor
                            by = trunk_edge - lay["loc_top"]
                            _emit_cluster(cid, bx, by, mirror_y=False, mirror_x=mx)
                        centers_this.append(rg.Point3d(bx + lay["w"] * 0.5, trunk_y, z))
                        depth_cursor += ch + shelf_gap
                    # record the trunk_gap channel center AFTER this column (the
                    # reserved gap between this column and the next) as a snap
                    # target, WITH the band's y-span so an empty gap still gets a
                    # corridor from the spine to the furthest branch in this band.
                    gx = cx + col_w + trunk_gap * 0.5
                    if band == 0:
                        gy_near, gy_far = trunk_y, trunk_y + trunk_gap * 0.5 + used_depth
                    else:
                        gy_near, gy_far = trunk_y - trunk_gap * 0.5 - used_depth, trunk_y
                    _gap_xs.append((gx, min(gy_near, gy_far), max(gy_near, gy_far)))
                    cx += col_w + trunk_gap
                    i = j
                return centers_this

            # emit a single cluster at world origin (bx, by). mirror_y flips the
            # cluster vertically about its local origin so the dense branch faces
            # the trunk (used for top-band clusters). mirror_x flips the cluster
            # HORIZONTALLY so rooms fill toward the building center instead of
            # left-to-right from the cluster origin.
            def _emit_cluster(cid, bx, by, mirror_y=False, mirror_x=False):
                lay = cluster_layout[cid]
                cw_local = lay["w"]
                cminx = 1e18; cminy = 1e18; cmaxx = -1e18; cmaxy = -1e18
                n_branches = len(lay["branches"])
                for bi, br in enumerate(lay["branches"]):
                    is_big = bool(br.get("big"))
                    branch_room_count = 0
                    _top_y_ext = []   # world y-values of top-row rooms
                    _bot_y_ext = []   # world y-values of bottom-row rooms
                    _br_x_ext = []    # world x-values of this branch's rooms
                    # SINGLE-LOADED: flip so the branch corridor + access edge sit
                    # on the TRUNK-facing side of the rooms, not the outer edge.
                    # The trunk is toward loc_top (dense-to-trunk pin). After the
                    # cluster's mirror_y, the trunk-facing vertical direction is:
                    #   top-band (mirror_y=True): trunk is BELOW -> corridor below rooms
                    #   bottom-band (mirror_y=False): trunk is ABOVE -> corridor above rooms
                    # For single-loaded, force the access edge to point that way.
                    sl_flip = bool(br.get("single_loaded"))
                    for gi, group in enumerate((br["top"], br["bot"])):
                        for p in group:
                            r = p["rec"]
                            w = p["w"]; h = p["h"]
                            if mirror_x:
                                x0 = bx + (cw_local - (p["x"] + w))
                            else:
                                x0 = bx + p["x"]
                            if mirror_y:
                                y0 = by - (p["y"] + h)
                                acc_up = not p["access_up"]
                            else:
                                y0 = by + p["y"]
                                acc_up = p["access_up"]
                            # single-loaded: corridor faces trunk. top-band trunk is
                            # below (acc_up False); bottom-band trunk is above (True).
                            if sl_flip:
                                acc_up = True if not mirror_y else False
                            plane = rg.Plane(rg.Point3d(x0, y0, z), rg.Vector3d.ZAxis)
                            rooms.Add(rg.Rectangle3d(plane, w, h).ToNurbsCurve(), path_out)
                            room_lbl.Add(label_by_uid.get(r["uid"], r["uid"]), path_out)
                            room_uid_out.Add(r["uid"], path_out)
                            room_dept_out.Add(r["dept"], path_out)
                            room_color_out.Add(r["color"], path_out)
                            cluster_id_out.Add(cid, path_out)
                            access_edge.Add(3 if acc_up else 1, path_out)
                            _fb[4] += 1
                            branch_room_count += 1
                            _room_spans.append((x0, x0 + w))
                            if gi == 0:
                                _top_y_ext.extend([y0, y0 + h])
                            else:
                                _bot_y_ext.extend([y0, y0 + h])
                            _br_x_ext.extend([x0, x0 + w])
                            cminx = min(cminx, x0); cminy = min(cminy, y0)
                            cmaxx = max(cmaxx, x0 + w); cmaxy = max(cmaxy, y0 + h)

                    # branch centerline. For single-loaded branches, put the
                    # centerline on the TRUNK-facing side of the rooms (so the
                    # corridor sits between rooms and trunk), not the outer edge,
                    # and span only the rooms' actual x-extent (they may be mirror_x'd).
                    bcy = (by - br["bc_y"]) if mirror_y else (by + br["bc_y"])
                    bx0 = bx
                    bx1 = bx + br["w"]
                    if sl_flip:
                        row_ys = _top_y_ext + _bot_y_ext
                        if row_ys:
                            if not mirror_y:
                                bcy = max(row_ys) + branch_gap * 0.5
                            else:
                                bcy = min(row_ys) - branch_gap * 0.5
                        if _br_x_ext:
                            bx0 = min(_br_x_ext)
                            bx1 = max(_br_x_ext)
                    # HIERARCHY DEDUP (Trunk Shelf > Trunk > Branch): if this branch
                    # corridor coincides with the shelf trunk line (within branch_gap),
                    # skip it -- the trunk already serves these rooms.
                    if abs(bcy - trunk_y) <= branch_gap:
                        pass  # overlaps shelf trunk -> eliminate branch corridor
                    else:
                        branch_ctr.Add(rg.Line(rg.Point3d(bx0, bcy, z),
                                               rg.Point3d(bx1, bcy, z)).ToNurbsCurve(), path_out)
                        bp = rg.Plane(rg.Point3d(bx0, bcy - branch_gap * 0.5, z), rg.Vector3d.ZAxis)
                        branch_rect.Add(rg.Rectangle3d(bp, bx1 - bx0, branch_gap).ToNurbsCurve(), path_out)

                    # --- COLLECT vertical corridors (emitted after consolidation)
                    # Connectors are NO LONGER emitted as standalone verticals --
                    # they routed through branch interiors (cutting rooms). Every
                    # branch reaches the trunk via a GAP corridor (between clusters)
                    # or a BREAK corridor (real room-split gap) instead, both of
                    # which sit in genuine gaps by construction.
                    # Break corridors: HALF-SPAN per row side. A top-row break
                    # runs from the branch centerline to the top row's far edge;
                    # a bottom-row break from centerline to the bottom edge. Each
                    # stays clear of the OTHER row's rooms; both meet the branch
                    # centerline corridor -> network stays connected.
                    def _bxk_world(bxk_local):
                        if mirror_x:
                            return bx + (cw_local - bxk_local)
                        return bx + bxk_local
                    for bxk_local in br.get("top_break_xs", []):
                        if _top_y_ext:
                            far = max(_top_y_ext, key=lambda yy: abs(yy - bcy))
                            ya, yb = sorted([bcy, far])
                            _vcorr.append(dict(x=_bxk_world(bxk_local), y0=ya, y1=yb,
                                               weight=int(branch_room_count),
                                               kind="break", prio=2))
                    for bxk_local in br.get("bot_break_xs", []):
                        if _bot_y_ext:
                            far = max(_bot_y_ext, key=lambda yy: abs(yy - bcy))
                            ya, yb = sorted([bcy, far])
                            _vcorr.append(dict(x=_bxk_world(bxk_local), y0=ya, y1=yb,
                                               weight=int(branch_room_count),
                                               kind="break", prio=2))

                if cmaxx > cminx:
                    cp = rg.Plane(rg.Point3d(cminx, cminy, z), rg.Vector3d.ZAxis)
                    cluster_rect_out.Add(rg.Rectangle3d(cp, cmaxx - cminx, cmaxy - cminy).ToNurbsCurve(), path_out)
                    _fb[0] = min(_fb[0], cminx); _fb[1] = min(_fb[1], cminy)
                    _fb[2] = max(_fb[2], cmaxx); _fb[3] = max(_fb[3], cmaxy)

            _fb = [floor_minx, floor_miny, floor_maxx, floor_maxy, 0]
            top_centers = _place_band(top_ids, 0, top_depth)
            bot_centers = _place_band(bot_ids, 1, bot_depth)
            floor_minx, floor_miny, floor_maxx, floor_maxy = _fb[0], _fb[1], _fb[2], _fb[3]
            total_rooms += _fb[4]

            # shelf trunk centerline: the spine, in the reserved band gap.
            # All verticals land on trunk_y by construction (shared coordinate).
            shelf_left = ox
            shelf_right = ox + shelf["width"]
            trunk_ctr.Add(rg.Line(rg.Point3d(shelf_left, trunk_y, z),
                                  rg.Point3d(shelf_right, trunk_y, z)).ToNurbsCurve(), path_out)
            # weight = total rooms this shelf's trunk serves
            shelf_room_ct = sum(cluster_layout[c]["branches"] and
                                sum(len(b["top"]) + len(b["bot"]) for b in cluster_layout[c]["branches"])
                                for c, _b in shelf["items"])
            trunk_weight.Add(int(shelf_room_ct), path_out)

            # advance to next shelf upward
            shelf_h = top_depth + trunk_gap + bot_depth
            shelf_y = shelf_y + shelf_h + shelf_gap

        # ---- CONSOLIDATE + EMIT vertical corridors (per floor) ------------
        # 1) every trunk_gap channel gets a corridor by default.
        # 2) breaks within snap_dist of a gap channel SNAP onto it.
        # 3) leftover verticals within snap_dist of each other MERGE.
        # Each resulting vertical spans from the shelf spine to the furthest
        # branch it serves (min y0 .. max y1 of the members snapped to it).
        # Result: one clean vertical per gap; mid-cluster breaks kept only when
        # no gap is near.
        gap_data = sorted(_gap_xs, key=lambda t: t[0])

        # Half-span BREAK corridors sit in real room gaps already -> emit them
        # directly (no bucket merge, which would stretch them to full height and
        # pull them onto gap-channel x). Only GAP-CHANNEL verticals go through
        # consolidation (dedup + perimeter test).
        breaks = [v for v in _vcorr if v.get("kind") == "break"]
        for v in breaks:
            if v["y1"] > v["y0"]:
                # skip breaks whose x is a true perimeter (no rooms one side)
                trunk_ctr.Add(rg.Line(rg.Point3d(v["x"], v["y0"], z),
                                      rg.Point3d(v["x"], v["y1"], z)).ToNurbsCurve(), path_out)
                trunk_weight.Add(int(max(v["weight"], 1)), path_out)

        # bucket GAP channels (dedup near-duplicates)
        buckets = []
        def _add(xv, prio, y0, y1, wt):
            for b in buckets:
                if abs(b["x"] - xv) <= snap_dist:
                    b["y0"] = min(b["y0"], y0)
                    b["y1"] = max(b["y1"], y1)
                    b["weight"] += wt
                    if prio > b["prio"]:
                        b["x"] = xv
                        b["prio"] = prio
                    return
            buckets.append(dict(x=xv, prio=prio, y0=y0, y1=y1, weight=wt))

        for (gx, gy0, gy1) in gap_data:
            _add(gx, 1, gy0, gy1, 0)

        # DROP true PERIMETER verticals only: a vertical is redundant if there
        # are NO rooms on one of its sides (it runs along the building edge). A
        # gap between two real clusters has rooms on both sides and is KEPT, even
        # if it sits near the bounding-box edge (e.g. beside a big extracted room).
        def _has_rooms_left(xv):
            for (rx0, rx1) in _room_spans:
                if rx1 <= xv + 1e-6 and (xv - rx1) < snap_dist * 3:
                    return True
            return False

        def _has_rooms_right(xv):
            for (rx0, rx1) in _room_spans:
                if rx0 >= xv - 1e-6 and (rx0 - xv) < snap_dist * 3:
                    return True
            return False

        emitted_v = 0
        for b in buckets:
            if b["y1"] <= b["y0"]:
                continue
            # keep only if rooms exist on BOTH sides within reach
            if not (_has_rooms_left(b["x"]) and _has_rooms_right(b["x"])):
                continue   # true perimeter -> redundant with building edge
            trunk_ctr.Add(rg.Line(rg.Point3d(b["x"], b["y0"], z),
                                  rg.Point3d(b["x"], b["y1"], z)).ToNurbsCurve(), path_out)
            trunk_weight.Add(int(max(b["weight"], 1)), path_out)
            emitted_v += 1

        # floor envelope
        if floor_maxx > floor_minx:
            ep = rg.Plane(rg.Point3d(floor_minx, floor_miny, z), rg.Vector3d.ZAxis)
            env_rect.Add(rg.Rectangle3d(ep, floor_maxx - floor_minx, floor_maxy - floor_miny).ToNurbsCurve(), path_out)
            extent = (floor_maxx - floor_minx) if spread_axis == 0 else (floor_maxy - floor_miny)
            floor_run += extent + floor_gap
            # efficiency: room area / envelope area for this floor
            env_area = (floor_maxx - floor_minx) * (floor_maxy - floor_miny)
            room_area = sum(r["area_model"] for r in recs)
            eff = (room_area / env_area * 100.0) if env_area > 0 else 0.0
            _eff_report.append("  Floor {} efficiency: {:.0f}% ({:.0f}x{:.0f}, aspect {:.2f})".format(
                rank, eff, (floor_maxx - floor_minx) / lin, (floor_maxy - floor_miny) / lin,
                (floor_maxx - floor_minx) / (floor_maxy - floor_miny) if (floor_maxy - floor_miny) else 0))

        lines.append("  Floor branch {{{}}} : {} rooms, {} clusters, order={}".format(
            rank, n, len(clusters), order))

    depts = sorted(d for d in dept_set if d is not None)

    header = ["Component 04 | Corridor Organizer (double-loaded, wrapping)",
              "Total rooms re-laid : {}".format(total_rooms),
              "Unit system: {}   model-units/input: {:.4f}".format(unit_system, lin),
              "Split: {}   room_depth={} {}  max_aspect={}:1".format(
                  split_mode, _get("room_depth", 28.0), unit_system, _get("max_aspect", 2.5)),
              "branch_gap={} trunk_gap={} branch_gap_v={} {}".format(
                  _get("branch_gap", 6.0), _get("trunk_gap", 8.0), _get("branch_gap_v", 8.0), unit_system),
              ("Split=bubble: adjacency chains kept contiguous (shared child beside parent)."
               if split_mode != "even" else
               "Split=even: legacy width-balanced rows (adjacency NOT preserved)."),
              "Labels loaded from lbl_in: {} (0 = falling back to uid; check lbl_in wiring/Access=Tree).".format(len(label_by_uid)),
              "Access edge PINNED FLAT; dense branch faces trunk; sub-stacked; big rooms extracted.",
              "Corridors: shelf-trunk spine + gap-channel verticals + {}ft break splits (half-span).".format(_get("max_corridor_len", 75.0))]
    header += _eff_report
    if weak_routed:
        header.append("WEAK adjacencies routed long ({}):".format(len(weak_routed)))
        for wr in weak_routed[:8]:
            header.append("  " + wr)
    return "\n".join(header + lines)


_do_run = True if ('run' not in globals() or run is None) else bool(run)
if _do_run:
    info = _build()
else:
    info = "Idle: set run=True to organize."
