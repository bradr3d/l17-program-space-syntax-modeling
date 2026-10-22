import xml.etree.ElementTree as ET
from collections import defaultdict
import json
import re
from pathlib import Path

path = (Path(__file__).resolve().parent / "ExcelToBubbleDiagram.ghx")
tree = ET.parse(path)
root = tree.getroot()


def items_dict(chunk):
    d = {}
    for item in chunk.findall("items/item"):
        name = item.get("name")
        if name is None:
            continue
        idx = item.get("index")
        text = (item.text or "").strip()
        if name == "ID" and idx is not None:
            d.setdefault("_IDs", []).append(text)
            continue
        if name == "Source":
            d.setdefault("_Sources", []).append(text)
            continue
        key = name if idx is None else f"{name}[{idx}]"
        if key in d:
            if not isinstance(d[key], list):
                d[key] = [d[key]]
            d[key].append(text)
        else:
            d[key] = text
    return d


def persistent_from_param(pch):
    pers = []
    pd = pch.find('chunks/chunk[@name="PersistentData"]')
    if pd is None:
        return pers
    for branch in pd.findall('.//chunk[@name="Branch"]'):
        for it in branch.findall('.//chunk[@name="Item"]'):
            for sub in it.findall("items/item"):
                if sub.text:
                    pers.append(sub.text.strip())
    for item in pd.findall(".//items/item"):
        n = item.get("name", "")
        if n in ("Count", "PathCount", "ItemCount") or not item.text:
            continue
        t = item.text.strip()
        if t and t not in pers:
            pers.append(t)
    return pers


meta = {}
for chunk in root.iter("chunk"):
    n = chunk.get("name")
    if n == "DocumentHeader":
        meta["DocumentHeader"] = items_dict(chunk)
    elif n == "DefinitionProperties":
        meta["DefinitionProperties"] = items_dict(chunk)
    elif n == "ValueTable":
        meta["ValueTable"] = items_dict(chunk)
    elif n == "GHALibraries":
        libs = []
        for lib in chunk.findall("chunks/chunk"):
            if lib.get("name") == "Library":
                libs.append(items_dict(lib))
        meta["Libraries"] = libs

objects = []
params = {}
PANEL_GUID = "59e0b89a-e487-49f8-bab8-b5bab16be14c"
BOOL_GUID = "2e78987b-9dfb-42a2-8b76-3923ac8bd91a"

for obj in root.findall('.//chunk[@name="DefinitionObjects"]/chunks/chunk[@name="Object"]'):
    obj_items = items_dict(obj)
    type_guid = obj_items.get("GUID", "")
    type_name = obj_items.get("Name", "")
    container = obj.find('chunks/chunk[@name="Container"]')
    if container is None:
        continue
    citems = items_dict(container)
    inst = citems.get("InstanceGuid", "")
    nick = citems.get("NickName", "")
    desc = citems.get("Description", "")

    rec = {
        "type_guid": type_guid,
        "type_name": type_name,
        "instance_guid": inst,
        "nickname": nick,
        "description": desc,
    }

    pp = container.find('chunks/chunk[@name="PanelProperties"]')
    if pp is not None:
        ppd = items_dict(pp)
        rec["panel_text"] = ppd.get("UserText", "")

    if type_name == "Group":
        rec["member_ids"] = citems.get("_IDs", [])
        objects.append(rec)
        continue

    if type_name == "Cluster":
        rec["is_cluster"] = True
        # cluster may have nested DefinitionObjects
        nested = obj.find('.//chunk[@name="DefinitionObjects"]')
        if nested is not None:
            rec["nested_object_count"] = items_dict(nested).get("ObjectCount", "?")

    inputs, outputs = [], []
    for pch in container.findall("chunks/chunk"):
        pname = pch.get("name", "")
        if not pname.startswith("param_"):
            continue
        pi = items_dict(pch)
        p_inst = pi.get("InstanceGuid", "")
        sources = list(pi.get("_Sources", []))
        pers = persistent_from_param(pch)
        p_rec = {
            "instance_guid": p_inst,
            "nickname": pi.get("NickName", ""),
            "kind": "input" if "input" in pname else "output",
            "sources": sources,
            "persistent": pers,
        }
        params[p_inst] = {
            **p_rec,
            "parent_instance": inst,
            "parent_nick": nick,
            "parent_type": type_name,
            "parent_type_guid": type_guid,
        }
        (inputs if "input" in pname else outputs).append(p_rec)

    # Component-level persistent (sliders, etc.)
    for key in ("Value", "Slider", "Minimum", "Maximum", "DecimalPlaces", "Expression"):
        if key in citems:
            rec.setdefault("container_persistent", {})[key] = citems[key]

    pd = container.find('chunks/chunk[@name="PersistentData"]')
    if pd is not None:
        rec["persistent_data"] = persistent_from_param(container)

    if type_guid == PANEL_GUID:
        pers = rec.get("persistent_data") or []
        if rec.get("panel_text") and rec["panel_text"] not in pers:
            pers = [rec["panel_text"]] + pers
        params[inst] = {
            "instance_guid": inst,
            "nickname": nick,
            "kind": "panel",
            "sources": citems.get("_Sources", []),
            "persistent": pers,
            "parent_type": "Panel",
            "parent_type_guid": type_guid,
        }
        rec["standalone_sources"] = citems.get("_Sources", [])

    if type_guid == BOOL_GUID:
        rec["toggle_value"] = citems.get("Value", "")
        params[inst] = {
            "instance_guid": inst,
            "nickname": nick,
            "kind": "toggle",
            "persistent": [citems.get("Value", "")],
            "parent_type": "Boolean Toggle",
            "parent_type_guid": type_guid,
        }

    rec["inputs"] = inputs
    rec["outputs"] = outputs
    objects.append(rec)

# Resolve guid -> label
guid_label = {}
for o in objects:
    if o["instance_guid"]:
        label = o.get("nickname") or o["type_name"]
        if o.get("panel_text"):
            label = f"Panel:{o['panel_text'][:40]}"
        guid_label[o["instance_guid"]] = label
for pid, p in params.items():
    pn = p.get("parent_nick") or p.get("parent_type", "")
    guid_label[pid] = f"{pn}.{p.get('nickname', p['kind'])}"

type_counts = defaultdict(int)
nick_by_type = defaultdict(list)
for o in objects:
    key = f"{o['type_name']} | {o['type_guid']}"
    type_counts[key] += 1
    if o.get("nickname") or o.get("panel_text"):
        nick_by_type[key].append(o.get("nickname") or o.get("panel_text", "")[:50])

wires = []
for pid, p in params.items():
    for src in p.get("sources", []):
        wires.append((src, pid))

# Export summary JSON
out = {
    "meta": meta,
    "object_count": len(objects),
    "type_counts": dict(type_counts),
    "groups": [
        {"instance": o["instance_guid"], "members": len(o.get("member_ids", [])), "nick": o.get("nickname")}
        for o in objects
        if o["type_name"] == "Group"
    ],
    "clusters": [
        o for o in objects if o.get("is_cluster") or o["type_name"] == "Cluster"
    ],
    "components_unique": [],
    "wires_sample": [],
    "panels_with_text": [],
}

seen = set()
for o in objects:
    if o["type_name"] == "Group":
        continue
    key = (o["type_guid"], o.get("nickname", ""), o.get("panel_text", "")[:30])
    if key in seen:
        continue
    seen.add(key)
    entry = {
        "type_name": o["type_name"],
        "type_guid": o["type_guid"],
        "nickname": o.get("nickname", ""),
        "instance": o["instance_guid"],
        "description": (o.get("description") or "")[:200],
        "inputs": [(i["nickname"], i.get("persistent")) for i in o.get("inputs", [])],
        "outputs": [i["nickname"] for i in o.get("outputs", [])],
    }
    if o.get("container_persistent"):
        entry["container_persistent"] = o["container_persistent"]
    if o.get("panel_text"):
        entry["panel_text"] = o["panel_text"]
    if o.get("toggle_value") is not None:
        entry["toggle_value"] = o.get("toggle_value")
    out["components_unique"].append(entry)

for o in objects:
    if o["type_guid"] == PANEL_GUID and o.get("panel_text"):
        out["panels_with_text"].append({"nick": o.get("nickname"), "text": o["panel_text"], "inst": o["instance_guid"]})

for src, tgt in wires[:500]:
    out["wires_sample"].append({"from": guid_label.get(src, src), "from_guid": src, "to": guid_label.get(tgt, tgt), "to_guid": tgt})
out["wire_count"] = len(wires)

with open((Path(__file__).resolve().parent / "_ghx_parse_out.json"), "w", encoding="utf-8") as f:
    json.dump(out, f, indent=2)

print("Object count:", len(objects))
print("Wire count:", len(wires))
print("\nTYPE COUNTS:")
for k, v in sorted(type_counts.items(), key=lambda x: -x[1]):
    print(f"  {v:3d}  {k}")
    nicks = nick_by_type[k]
    if nicks:
        uniq = list(dict.fromkeys(nicks))
        print(f"       -> {uniq[:20]}")

print("\nGROUPS:")
for o in objects:
    if o["type_name"] == "Group":
        print(f"  members={len(o.get('member_ids',[]))} inst={o['instance_guid']}")

print("\nCLUSTERS:", len(out["clusters"]))
for c in out["clusters"]:
    print(" ", c.get("nickname"), c.get("type_guid"), c.get("instance_guid"))

print("\nWritten _ghx_parse_out.json")
