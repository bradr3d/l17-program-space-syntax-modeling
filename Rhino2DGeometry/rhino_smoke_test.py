#! python3
import os
import Rhino
import System


plugin_path = os.path.join(os.path.dirname(__file__), "bin", "Release", "Rhino2DGeometry.rhp")
plugin_id = System.Guid("7e49847f-020b-4d92-814b-8e77140e670b")
load_result, plugin_id = Rhino.PlugIns.PlugIn.LoadPlugIn(plugin_path, plugin_id)
print("Plugin load: {} ({})".format(load_result, plugin_id))
if int(load_result) not in (0, 1):
    # 0 = SuccessAlreadyLoaded, 1 = Success
    raise RuntimeError("Could not load Rhino2DGeometry.rhp: {}".format(load_result))

if not Rhino.RhinoApp.RunScript("_Test2DFloorplanEngine", False):
    raise RuntimeError("Test2DFloorplanEngine could not be executed.")
print("Self-test command executed.")

doc = Rhino.RhinoDoc.ActiveDoc
before = doc.Objects.Count
if not Rhino.RhinoApp.RunScript("_Create2DFloorplanFixture", False):
    raise RuntimeError("Create2DFloorplanFixture could not be executed.")

for obj in doc.Objects:
    if obj.Attributes.GetUserString("UID"):
        obj.Select(True)

if not Rhino.RhinoApp.RunScript("_Generate2DFloorplan", False):
    raise RuntimeError("Generate2DFloorplan could not be executed.")

after = doc.Objects.Count
layers = [layer.FullPath for layer in doc.Layers if not layer.IsDeleted]
required = (
    "Floorplan",
    "Rooms",
    "Corridors",
    "Branch",
    "Trunk",
    "Doors",
    "Diagnostics",
)
missing = [name for name in required if not any(name in path for path in layers)]
if missing:
    raise RuntimeError("Missing layers: {}".format(missing))
if after <= before:
    raise RuntimeError("Generate2DFloorplan did not add geometry.")
print("Smoke test added {} objects. Layers: {}".format(after - before, len(layers)))
print("Rhino2DGeometry smoke test passed.")
