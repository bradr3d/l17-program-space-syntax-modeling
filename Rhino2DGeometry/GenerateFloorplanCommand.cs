using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace SeniorDesign.Rhino2DGeometry
{
    public sealed class GenerateFloorplanCommand : Command
    {
        public override string EnglishName { get { return "Generate2DFloorplan"; } }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var settings = new GenerationSettings();
            settings.UnitScale = RhinoMath.UnitScale(UnitSystem.Feet, doc.ModelUnitSystem);
            if (!RhinoMath.IsValidDouble(settings.UnitScale) || settings.UnitScale <= 0)
                settings.UnitScale = 1.0;

            var scripted = mode == RunMode.Scripted;
            if (!scripted && !GetSettings(settings))
                return Result.Cancel;
            if (!scripted && !GetOrigin(settings))
                return Result.Cancel;

            List<RhinoObject> selected;
            var selectionResult = scripted
                ? SelectedObjects(doc, out selected)
                : SelectBubbles(out selected);
            if (selectionResult != Result.Success)
                return selectionResult;

            uint undo = 0;
            try
            {
                var bubbles = BubbleMetadataReader.Read(selected, settings.DefaultDoorWidth);
                RhinoApp.WriteLine("Generating rooms and clusters from " + bubbles.Count + " bubbles...");
                var result = FloorplanPipeline.Generate(bubbles, settings, doc.ModelAbsoluteTolerance);

                undo = doc.BeginUndoRecord("Generate 2D Floorplan");
                var objectCount = DocumentWriter.Write(doc, result, settings);
                foreach (var warning in result.Warnings)
                    RhinoApp.WriteLine("Warning: " + warning);
                RhinoApp.WriteLine(
                    "2D floorplan created: " + result.Rooms.Count + " rooms, " +
                    result.Corridors.Count + " corridor segments, " +
                    result.Doors.Count + " doors, " + result.FloorCount +
                    " floors, " + objectCount + " Rhino objects.");
                RhinoApp.WriteLine(
                    "Plate containment is area-based only; non-rectangular outline fitting is not implemented.");
                return Result.Success;
            }
            catch (Exception exception)
            {
                RhinoApp.WriteLine("Generate2DFloorplan failed: " + exception.Message);
                return Result.Failure;
            }
            finally
            {
                if (undo != 0)
                    doc.EndUndoRecord(undo);
            }
        }

        private static Result SelectedObjects(RhinoDoc doc, out List<RhinoObject> selected)
        {
            selected = doc.Objects.GetSelectedObjects(false, false).ToList();
            if (selected.Count == 0)
            {
                RhinoApp.WriteLine("Generate2DFloorplan: no objects are pre-selected.");
                return Result.Nothing;
            }
            return Result.Success;
        }

        private static Result SelectBubbles(out List<RhinoObject> selected)
        {
            selected = new List<RhinoObject>();
            using (var get = new GetObject())
            {
                get.SetCommandPrompt("Select metadata-tagged bubble circles");
                get.GeometryFilter = ObjectType.Curve;
                get.GroupSelect = true;
                get.SubObjectSelect = false;
                get.EnablePreSelect(true, true);
                var result = get.GetMultiple(1, 0);
                if (result == GetResult.Cancel)
                    return Result.Cancel;
                if (result != GetResult.Object)
                    return Result.Failure;
                for (var i = 0; i < get.ObjectCount; i++)
                {
                    var obj = get.Object(i).Object();
                    if (obj != null)
                        selected.Add(obj);
                }
            }
            return Result.Success;
        }

        private static bool GetOrigin(GenerationSettings settings)
        {
            using (var get = new GetPoint())
            {
                get.SetCommandPrompt("Floorplan output origin, or press Enter for World origin");
                get.SetDefaultPoint(settings.Origin);
                get.AcceptNothing(true);
                var result = get.Get();
                if (result == GetResult.Cancel)
                    return false;
                if (result == GetResult.Point)
                    settings.Origin = get.Point();
            }
            settings.Origin.Z = 0;
            return true;
        }

        private static bool GetSettings(GenerationSettings settings)
        {
            var plateWidth = new OptionDouble(settings.PlateWidth, 1, 100000);
            var plateLength = new OptionDouble(settings.PlateLength, 1, 100000);
            var efficiency = new OptionDouble(settings.FloorEfficiency, 0.05, 1.0);
            var clusters = new OptionInteger(settings.ClusterCount, 1, 100);
            var maxFloors = new OptionInteger(settings.MaxFloors, 1, 200);
            var roomDepth = new OptionDouble(settings.RoomDepth, 1, 1000);
            var maxRun = new OptionDouble(settings.MaxCorridorLength, 5, 10000);
            var branchWidth = new OptionDouble(settings.BranchWidth, 1, 100);
            var trunkWidth = new OptionDouble(settings.TrunkBaseWidth, 1, 100);
            var doorWidth = new OptionDouble(settings.DefaultDoorWidth, 1, 50);

            while (true)
            {
                using (var get = new GetOption())
                {
                    get.SetCommandPrompt("Floorplan settings; press Enter to continue");
                    get.AcceptNothing(true);
                    get.AddOptionDouble("PlateWidth", ref plateWidth);
                    get.AddOptionDouble("PlateLength", ref plateLength);
                    get.AddOptionDouble("Efficiency", ref efficiency);
                    get.AddOptionInteger("Clusters", ref clusters);
                    get.AddOptionInteger("MaxFloors", ref maxFloors);
                    get.AddOptionDouble("RoomDepth", ref roomDepth);
                    get.AddOptionDouble("MaxCorridorRun", ref maxRun);
                    get.AddOptionDouble("BranchWidth", ref branchWidth);
                    get.AddOptionDouble("TrunkBaseWidth", ref trunkWidth);
                    get.AddOptionDouble("DoorWidth", ref doorWidth);
                    var result = get.Get();
                    if (result == GetResult.Cancel)
                        return false;
                    if (result == GetResult.Nothing)
                        break;
                }
            }

            settings.PlateWidth = plateWidth.CurrentValue;
            settings.PlateLength = plateLength.CurrentValue;
            settings.FloorEfficiency = efficiency.CurrentValue;
            settings.ClusterCount = clusters.CurrentValue;
            settings.MaxFloors = maxFloors.CurrentValue;
            settings.RoomDepth = roomDepth.CurrentValue;
            settings.MaxCorridorLength = maxRun.CurrentValue;
            settings.BranchWidth = branchWidth.CurrentValue;
            settings.TrunkBaseWidth = trunkWidth.CurrentValue;
            settings.DefaultDoorWidth = doorWidth.CurrentValue;
            return true;
        }
    }
}
