using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace ProgramSyntaxPlugin
{
    public class ProgramSyntaxCommand : Command
    {
        public override string EnglishName => "ProgramSyntaxOpen"; // USE this to open the panel in RHINO via command line!!

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var panelId = typeof(ProgramSyntaxPanel).GUID;
            Panels.OpenPanel(panelId);
            return Result.Success;
        }
    }
}