#! python3
# r: openpyxl
# ==============================================================================
# COMPONENT 01  |  PROGRAM READER  (Excel -> Room Instances)
# ------------------------------------------------------------------------------
# Rhino 8 / Grasshopper CPython script component.
# Reads Sheet2 of the program .xlsm, cleans it, explodes Qty into individual
# room instances, and wires directed adjacency pairwise-by-index. Emits flat,
# index-aligned lists that Component 02 (bubble solver) consumes.
#
# ------------------------------------------------------------------------------
# INPUTS  (set Type Hint + Access exactly as noted; right-click each param)
#   xlsx_path   : str   | Item   | Full path to the .xlsm program file.
#   sheet_name  : str   | Item   | Worksheet to read. Default "Sheet2".
#   header_row  : int   | Item   | 1-based row holding the column headers. Default 1.
#   skip_zero   : bool  | Item   | True -> drop rows whose Qty == 0. Default True.
#   run         : bool  | Item   | Toggle to (re)read the file. Default True.
#
# OUTPUTS (all List Access, index-aligned 1:1 across the room outputs)
#   uid         : str   | List   | Stable instance id "{DeptNo}#{i}"  (unique).
#   label       : str   | List   | Display label "{DeptNo} {ProgramElement} (i/N)".
#   dept        : int   | List   | Leading dept token before first dot (7.10 -> 7).
#   dept_raw    : str   | List   | Original Dept No string, preserved for labels.
#   area        : float | List   | Net SF per single instance (NOT x Qty).
#   floor       : int   | List   | Floor number (nan floors coerced to 1).
#   color       : Colour| List   | System.Drawing.Color from "R, G, B".
#   shared_out  : bool  | List   | Shared Access flag: room connects directly to
#                                  its adjacency parent (no corridor door). Reads
#                                  the optional "Shared Access" column; TRUE/X/yes
#                                  -> True. Absent column -> all False.
#                                  NOTE: output param is named 'shared_out' to
#                                  avoid colliding with the internal read.
#   adj_source  : str   | List   | uid of THIS room (edge tail).
#   adj_target  : str   | List   | uid of the Conn.Primary room (edge head).
#                                  Wired pairwise-by-index, clamped to target N.
#   depts       : int   | List   | Sorted unique dept tokens (passthrough for 02/03).
#   info        : str   | Item   | Diagnostics string (counts, skips, warnings).
#
# ------------------------------------------------------------------------------
# CONVENTIONS / GOTCHAS (CPython Rhino 8)
#   - "#! python3" shebang required on line 1.
#   - Set geometry/type hints on the params; do not rely on .Item[j] indexers.
#   - openpyxl ships with Rhino 8 CPython; if missing, info reports it cleanly.
#   - Qty explodes ALL attributes N times (area is per-instance, unchanged).
#   - Adjacency is directed: source PULLS toward target (see Comp 02).
#   - Pairwise wiring: instance #k of A -> instance #min(k, Nb-1) of B.
# ==============================================================================

import System
import math

# ---- import openpyxl defensively -------------------------------------------
_OPENPYXL_OK = True
_OPENPYXL_ERR = ""
try:
    import openpyxl
except Exception as e:
    _OPENPYXL_OK = False
    _OPENPYXL_ERR = str(e)


# ---- helpers ---------------------------------------------------------------
def _s(v):
    """Cell -> clean string ('' for None)."""
    if v is None:
        return ""
    if isinstance(v, float) and math.isnan(v):
        return ""
    return str(v).strip()


def _num(v, default=None):
    """Cell -> float, tolerant of strings/blanks; returns default on failure."""
    if v is None:
        return default
    if isinstance(v, (int, float)):
        if isinstance(v, float) and math.isnan(v):
            return default
        return float(v)
    txt = str(v).strip().replace(",", "")
    if txt == "":
        return default
    try:
        return float(txt)
    except ValueError:
        return default


def _dept_token(dept_raw):
    """Leading numeric token before the first dot. '7.10'->7, '1.1.2'->1."""
    txt = _s(dept_raw)
    if txt == "":
        return -1
    head = txt.split(".")[0]
    try:
        return int(head)
    except ValueError:
        return -1


def _parse_color(rgb_text):
    """'168, 174, 73' -> System.Drawing.Color. Falls back to mid-grey."""
    txt = _s(rgb_text)
    try:
        parts = [int(float(p.strip())) for p in txt.split(",")]
        if len(parts) >= 3:
            r, g, b = (max(0, min(255, parts[k])) for k in range(3))
            return System.Drawing.Color.FromArgb(255, r, g, b)
    except Exception:
        pass
    return System.Drawing.Color.FromArgb(255, 128, 128, 128)


def _is_white(rgb_text):
    """Spacer rows carry 255,255,255."""
    txt = _s(rgb_text)
    try:
        parts = [int(float(p.strip())) for p in txt.split(",")]
        return len(parts) >= 3 and parts[0] == 255 and parts[1] == 255 and parts[2] == 255
    except Exception:
        return False


# ---- column resolution (tolerant of trailing spaces / minor renames) -------
def _find_col(headers, *candidates):
    norm = {h.strip().lower(): i for i, h in enumerate(headers)}
    for cand in candidates:
        key = cand.strip().lower()
        if key in norm:
            return norm[key]
    # loose contains-match fallback
    for cand in candidates:
        key = cand.strip().lower()
        for h, i in norm.items():
            if key in h:
                return i
    return -1


# ---- main ------------------------------------------------------------------
uid = []
label = []
dept = []
dept_raw = []
area = []
floor = []
color = []
shared_out = []
adj_source = []
adj_target = []
depts = []
info = ""

_warn = []


def _build():
    global info
    lines = []

    if not _OPENPYXL_OK:
        return ("ERROR: openpyxl unavailable in this CPython env.\n{}\n"
                "FIX: ensure line 2 of this script reads  # r: openpyxl  then re-solve\n"
                "(first solve downloads it; needs PyPI access). Manual fallback:\n"
                "  <rhino-python.exe> -m pip install openpyxl").format(_OPENPYXL_ERR)

    path = _s(xlsx_path) if 'xlsx_path' in globals() and xlsx_path else ""
    if path == "":
        return "Waiting: no xlsx_path supplied."

    sheet = _s(sheet_name) if 'sheet_name' in globals() and sheet_name else "Sheet2"
    hrow = int(header_row) if ('header_row' in globals() and header_row) else 1
    do_skip_zero = True if ('skip_zero' not in globals() or skip_zero is None) else bool(skip_zero)

    try:
        wb = openpyxl.load_workbook(path, data_only=True, keep_vba=True, read_only=True)
    except Exception as e:
        return "ERROR loading workbook: {}".format(e)

    if sheet not in wb.sheetnames:
        return "ERROR: sheet '{}' not found. Available: {}".format(sheet, ", ".join(wb.sheetnames))

    ws = wb[sheet]
    rows = list(ws.iter_rows(values_only=True))
    if len(rows) < hrow:
        return "ERROR: sheet has fewer rows than header_row."

    headers = [_s(h) for h in rows[hrow - 1]]
    c_dept = _find_col(headers, "Dept No")
    c_name = _find_col(headers, "Program Element")
    c_qty = _find_col(headers, "Room Qty/ Units", "Room Qty", "Qty")
    c_net = _find_col(headers, "Net SF/ Units", "Net SF")
    c_conn = _find_col(headers, "Conn. Primary", "Conn Primary", "Conn")
    c_flr = _find_col(headers, "Flr", "Floor")
    c_rgb = _find_col(headers, "Color RGB", "RGB")
    c_shared = _find_col(headers, "Shared Access", "SharedAccess", "Shared")

    missing = [n for n, i in [("Dept No", c_dept), ("Program Element", c_name),
                              ("Qty", c_qty), ("Net SF", c_net),
                              ("Conn.Primary", c_conn), ("Flr", c_flr)] if i < 0]
    if missing:
        return "ERROR: missing columns: {}".format(", ".join(missing))

    # ---- PASS 1: clean rows into records ----------------------------------
    records = []          # dict per surviving source row
    n_white = 0
    n_zero = 0
    n_blank = 0
    for r in rows[hrow:]:
        d_raw = _s(r[c_dept]) if c_dept < len(r) else ""
        name = _s(r[c_name]) if c_name < len(r) else ""
        if d_raw == "" and name == "":
            n_blank += 1
            continue
        rgb_txt = _s(r[c_rgb]) if (c_rgb >= 0 and c_rgb < len(r)) else ""
        if _is_white(rgb_txt):
            n_white += 1
            continue
        qty = _num(r[c_qty], 0) if c_qty < len(r) else 0
        qty = int(qty) if qty is not None else 0
        if do_skip_zero and qty <= 0:
            n_zero += 1
            continue
        if qty <= 0:
            qty = 1
        net = _num(r[c_net], 0.0) if c_net < len(r) else 0.0
        conn = _s(r[c_conn]) if c_conn < len(r) else ""
        flr = _num(r[c_flr], None) if c_flr < len(r) else None
        flr = 1 if flr is None else int(flr)
        col = _parse_color(rgb_txt)
        tok = _dept_token(d_raw)
        # Shared Access: TRUE/X marks a room that connects DIRECTLY to its
        # adjacency parent (restroom -> classroom) with no corridor door.
        # Absent column -> all False (everyone gets a corridor door).
        shared_txt = _s(r[c_shared]) if (c_shared >= 0 and c_shared < len(r)) else ""
        shared = shared_txt.strip().lower() in ("true", "x", "yes", "1", "shared")
        records.append(dict(d_raw=d_raw, name=name, qty=qty, net=net,
                            conn=conn, flr=flr, col=col, tok=tok, shared=shared))

    if not records:
        return "ERROR: no usable rows after cleaning."

    # ---- PASS 2: explode Qty into instances -------------------------------
    # instances_by_dept: raw dept string -> ordered list of uids.
    # A Dept No string can legitimately repeat in the source (Excel collapsed
    # "7.10" -> "7.1", so two different rooms share "7.1"). Duplicate uids would
    # corrupt every name-based lookup downstream, so we disambiguate the SECOND
    # and later occurrences with a group-letter suffix on the uid namespace
    # ("7.1", then "7.1b", "7.1c", ...). The display label is untouched.
    instances_by_dept = {}
    inst = []              # ordered list of instance dicts
    _seen_dept_rows = {}   # d_raw -> how many source rows have used it
    _dupe_notes = []
    for rec in records:
        base = rec["d_raw"]
        seen = _seen_dept_rows.get(base, 0)
        _seen_dept_rows[base] = seen + 1
        if seen == 0:
            key = base                                   # first occurrence keeps the clean key
        else:
            key = "{}{}".format(base, chr(ord('a') + seen))  # 7.1 -> 7.1b, 7.1c...
            _dupe_notes.append("'{}' row #{} -> uid namespace '{}' ({})".format(
                base, seen + 1, key, rec["name"]))
        n = rec["qty"]
        ids = []
        for k in range(n):
            u = "{}#{}".format(key, k)
            ids.append(u)
            inst.append(dict(uid=u, d_raw=base, name=rec["name"],
                             net=rec["net"], flr=rec["flr"], col=rec["col"],
                             tok=rec["tok"], conn=rec["conn"], idx=k, qty=n,
                             shared=rec["shared"]))
        instances_by_dept[key] = list(ids)
        # also register under the clean base for adjacency lookups that target
        # the department string; first occurrence wins as the canonical target.
        if base not in instances_by_dept:
            instances_by_dept[base] = list(ids)

    # ---- PASS 3: wire directed adjacency, pairwise-by-index ---------------
    unresolved = []
    for it in inst:
        tgt_dept = _s(it["conn"])
        if tgt_dept == "" or tgt_dept == "0":
            continue
        tgt_list = instances_by_dept.get(tgt_dept, None)
        if not tgt_list:
            unresolved.append("{} -> {}".format(it["uid"], tgt_dept))
            continue
        j = it["idx"] if it["idx"] < len(tgt_list) else (len(tgt_list) - 1)
        adj_source.append(it["uid"])
        adj_target.append(tgt_list[j])

    # ---- emit room lists (index-aligned) ----------------------------------
    dept_set = set()
    for it in inst:
        n = it["qty"]
        uid.append(it["uid"])
        label.append("{} {} ({}/{})".format(it["d_raw"], it["name"], it["idx"] + 1, n))
        dept.append(it["tok"])
        dept_raw.append(it["d_raw"])
        area.append(float(it["net"]))
        floor.append(int(it["flr"]))
        color.append(it["col"])
        shared_out.append(bool(it["shared"]))
        dept_set.add(it["tok"])

    for d in sorted(dept_set):
        depts.append(d)

    lines.append("Component 01 | Program Reader")
    lines.append("Source rows kept : {}".format(len(records)))
    lines.append("Room instances   : {}".format(len(inst)))
    lines.append("Adjacency edges  : {}".format(len(adj_source)))
    lines.append("Departments      : {}".format(", ".join(str(d) for d in sorted(dept_set))))
    lines.append("Skipped -> white spacers: {}, qty=0: {}, blank: {}".format(n_white, n_zero, n_blank))
    if _dupe_notes:
        lines.append("NOTE duplicate Dept No disambiguated ({}):".format(len(_dupe_notes)))
        for dn in _dupe_notes:
            lines.append("  " + dn)
    if unresolved:
        lines.append("WARN unresolved adj targets ({}) [target skipped as qty=0 or absent]:".format(len(unresolved)))
        lines.append("  " + "; ".join(unresolved[:12]) + (" ..." if len(unresolved) > 12 else ""))
    floors_present = sorted(set(it["flr"] for it in inst))
    lines.append("Floors present   : {}".format(", ".join(str(f) for f in floors_present)))
    return "\n".join(lines)


# gate on run toggle
_do_run = True if ('run' not in globals() or run is None) else bool(run)
if _do_run:
    info = _build()
else:
    info = "Idle: set run=True to read the program file."
