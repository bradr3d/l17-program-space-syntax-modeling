using Rhino.PlugIns;
using Rhino.UI;

namespace ProgramSyntaxPlugin
{
    public class ProgramSyntaxPlugIn : PlugIn
    {
        public static ProgramSyntaxPlugIn? Instance { get; private set; }

        public ProgramSyntaxPlugIn()
        {
            Instance = this;
        }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // Registers your panel into the Rhino UI container
            Panels.RegisterPanel(this, typeof(ProgramSyntaxPanel), "Program Syntax", null);
            return LoadReturnCode.Success;
        }
    }
}