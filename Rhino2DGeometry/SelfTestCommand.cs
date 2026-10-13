using System;
using Rhino;
using Rhino.Commands;

namespace SeniorDesign.Rhino2DGeometry
{
    public sealed class FloorplanSelfTestCommand : Command
    {
        public override string EnglishName { get { return "Test2DFloorplanEngine"; } }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            try
            {
                RhinoApp.WriteLine(EngineSelfTests.Run());
                if (doc != null)
                    RhinoApp.WriteLine(EngineSelfTests.RunBake(doc));
                return Result.Success;
            }
            catch (Exception exception)
            {
                RhinoApp.WriteLine("Test2DFloorplanEngine failed: " + exception.Message);
                return Result.Failure;
            }
        }
    }
}
