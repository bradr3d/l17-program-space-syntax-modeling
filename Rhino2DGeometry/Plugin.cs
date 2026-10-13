using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;

namespace SeniorDesign.Rhino2DGeometry
{
    [Guid("7e49847f-020b-4d92-814b-8e77140e670b")]
    public sealed class Rhino2DGeometryPlugin : PlugIn
    {
        static Rhino2DGeometryPlugin()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveGrasshopperAssemblies;
        }

        public static Rhino2DGeometryPlugin Instance { get; private set; }

        public Rhino2DGeometryPlugin()
        {
            Instance = this;
        }

        private static Assembly ResolveGrasshopperAssemblies(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name != "KangarooSolver" && name != "Grasshopper" && name != "GH_IO")
                return null;

            var rhinoDir = Path.GetDirectoryName(typeof(RhinoApp).Assembly.Location);
            if (string.IsNullOrEmpty(rhinoDir))
                return null;

            var rhinoRoot = Path.GetFullPath(Path.Combine(rhinoDir, "..", ".."));
            var path = name == "KangarooSolver"
                ? Path.Combine(rhinoRoot, "Plug-ins", "Grasshopper", "Components", "KangarooSolver.dll")
                : Path.Combine(rhinoRoot, "Plug-ins", "Grasshopper", name + ".dll");

            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}
