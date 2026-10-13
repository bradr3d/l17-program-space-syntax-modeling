#! python3
# ==============================================================================
# COMPONENT 05  |  CORRIDOR WIDTH PASS  (branch / trunk ribbons)
# ------------------------------------------------------------------------------
# Rhino 8 / Grasshopper CPython script component.
# Takes the corridor SKELETON from Component 04 (branch centerlines + the shelf-
# trunk spine + channel-routed verticals) and offsets it into corridor RIBBONS
# with real widths. Branch/Trunk/Shelf corridors fulfill all circulation needs;
# there are no stem corridors.
#
# The skeleton is already connected BY CONSTRUCTION on shared coordinates
# (every connector endpoint lands exactly on the trunk spine), so this component
# only assigns width and picks up stragglers -- it never recomputes connectivity
# or matches floating-point endpoints. That is what avoids the fragmentation the
# older pipeline hit.
#
# CORRIDOR TYPES:
#   BRANCH : centerlines running between the two room rows of a branch.
#            Fixed default width (branch corridors carry one branch's rooms).
#   TRUNK  : the shelf spine + channel-routed verticals + break channels.
#            TRAFFIC-WIDENED: width = base + step per room served over a threshold
#            (from Comp04 trunk_weight).
#
# ------------------------------------------------------------------------------
# INPUTS
#   branch_ctr  : Curve | Tree | from 04 (branch centerlines, per floor)
#   trunk_ctr   : Curve | Tree | from 04 (spine + verticals + breaks)
#   trunk_weight: int   | Tree | from 04 (rooms served per trunk segment)
#
#   unit_system : str   | Item | "feet" or "meters"
#   branch_w    : float | Item | branch corridor width. Default 3.0
#   trunk_base_w: float | Item | trunk base width. Default 3.0
#   trunk_step  : float | Item | width added per room over threshold. Default 0.5
#   trunk_thresh: int   | Item | rooms served before widening starts. Default 4
#   trunk_max_w : float | Item | max trunk width cap. Default 8.0
#   run         : bool  | Item | solve toggle
#
# ------------------------------------------------------------------------------
# OUTPUTS (GRAFTED PER FLOOR, branch = floor rank)
#   branch_corr : Curve | Tree | branch corridor ribbons (closed rectangles)
#   trunk_corr  : Curve | Tree | trunk corridor ribbons (closed rectangles)
#   corr_all    : Curve | Tree | all corridor ribbons merged (for downstream)
#   branch_cl   : Curve | Tree | branch centerlines (passthrough for doors)
#   trunk_cl    : Curve | Tree | trunk centerlines (passthrough)
#   trunk_w_out : float | Tree | resolved trunk width per segment
#   info        : str   | Item | diagnostics
#
# ------------------------------------------------------------------------------
# CONVENTIONS / GOTCHAS
#   - "#! python3" required. Rhino.Geometry throughout. Tree Access on all Trees.
#   - Ribbons are closed rectangle curves centered on the centerline (offset both
#     sides by width/2). Axis-aligned skeleton -> ribbons are axis-aligned rects.
#   - No connectivity recomputation: the skeleton is already a single network.
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


def _line_endpoints(crv):
    """Return (p0, p1) for a line-like curve."""
    return crv.PointAtStart, crv.PointAtEnd


def _ribbon(p0, p1, width, z=0.0):
    """Closed rectangle ribbon centered on segment p0->p1, offset width/2 each side.
    Skeleton is axis-aligned so the perpendicular is clean."""
    dx = p1.X - p0.X
    dy = p1.Y - p0.Y
    ln = math.hypot(dx, dy)
    if ln < 1e-9:
        return None
    ux, uy = dx / ln, dy / ln
    # perpendicular
    px, py = -uy, ux
    hw = width * 0.5
    a = rg.Point3d(p0.X + px * hw, p0.Y + py * hw, z)
    b = rg.Point3d(p1.X + px * hw, p1.Y + py * hw, z)
    c = rg.Point3d(p1.X - px * hw, p1.Y - py * hw, z)
    d = rg.Point3d(p0.X - px * hw, p0.Y - py * hw, z)
    pl = rg.Polyline([a, b, c, d, a])
    return pl.ToNurbsCurve()


# ---- outputs ---------------------------------------------------------------
branch_corr = DataTree[object]()
trunk_corr = DataTree[object]()
corr_all = DataTree[object]()
branch_cl = DataTree[object]()
trunk_cl = DataTree[object]()
trunk_w_out = DataTree[object]()
info = ""


def _build():
    unit_system = _get("unit_system", "feet")
    lin = _unit_scale(unit_system)
    branch_w = float(_get("branch_w", 3.0)) * lin
    trunk_base_w = float(_get("trunk_base_w", 3.0)) * lin
    trunk_step = float(_get("trunk_step", 0.5)) * lin
    trunk_thresh = int(_get("trunk_thresh", 4))
    trunk_max_w = float(_get("trunk_max_w", 8.0)) * lin

    br_b = _branches(_get("branch_ctr", None))
    if not br_b:
        return "Waiting: no branch_ctr tree from Component 04."
    tr_b = dict((str(p), v) for p, v in _branches(_get("trunk_ctr", None)))
    tw_b = dict((str(p), v) for p, v in _branches(_get("trunk_weight", None)))

    total_branch = 0
    total_trunk = 0

    for (path, br_list) in br_b:
        key = str(path)
        rank = path.Indices[0] if path.Indices else 0
        pout = GH_Path(rank)

        # ---- BRANCH ribbons ------------------------------------------------
        for crv in br_list:
            p0, p1 = _line_endpoints(crv)
            rib = _ribbon(p0, p1, branch_w)
            if rib:
                branch_corr.Add(rib, pout)
                corr_all.Add(rib, pout)
                branch_cl.Add(crv, pout)
                total_branch += 1

        # ---- TRUNK ribbons (traffic-widened) -------------------------------
        tlist = tr_b.get(key, [])
        wlist = tw_b.get(key, [])
        for i, crv in enumerate(tlist):
            p0, p1 = _line_endpoints(crv)
            served = int(wlist[i]) if i < len(wlist) else 0
            extra = max(0, served - trunk_thresh) * trunk_step
            tw = min(trunk_max_w, trunk_base_w + extra)
            rib = _ribbon(p0, p1, tw)
            if rib:
                trunk_corr.Add(rib, pout)
                corr_all.Add(rib, pout)
                trunk_cl.Add(crv, pout)
                trunk_w_out.Add(tw / lin, pout)   # report in input units
                total_trunk += 1

    lines = ["Component 05 | Corridor Width Pass",
             "Unit system: {}   model-units/input: {:.4f}".format(unit_system, lin),
             "Branch ribbons: {}   Trunk ribbons: {}".format(total_branch, total_trunk),
             "Widths (input units): branch={}  trunk={}..{} (base+{}/room over {})".format(
                 _get("branch_w", 3.0), _get("trunk_base_w", 3.0), _get("trunk_max_w", 8.0),
                 _get("trunk_step", 0.5), _get("trunk_thresh", 4)),
             "Branch/Trunk/Shelf corridors only (stems removed)."]
    return "\n".join(lines)


_do_run = True if ('run' not in globals() or run is None) else bool(run)
if _do_run:
    info = _build()
else:
    info = "Idle: set run=True to build corridors."
