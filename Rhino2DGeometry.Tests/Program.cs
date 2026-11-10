using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Rhino;
using Rhino.Runtime.InProcess;
using SeniorDesign.Rhino2DGeometry;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rhino 8", "System");
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                var file = Path.Combine(system, "netcore", name.Name + ".dll");
                if (!File.Exists(file))
                    file = Path.Combine(system, name.Name + ".dll");
                return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
            };
            // Load the installed assembly before any test method binds Rhino types.
            AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(system, "netcore", "RhinoCommon.dll"));
            if (Array.IndexOf(args, "--rhino") >= 0)
            {
                SetDllDirectory(system);
                Environment.SetEnvironmentVariable("PATH", system + ";" + Environment.GetEnvironmentVariable("PATH"));
            }
            return RunTests(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Test2DFloorplanEngine failed: " + exception);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunTests(string[] args)
    {
        Console.WriteLine(ExcelReaderTests.Run());
        Console.WriteLine(BubbleLayoutTests.RunCollision());
        Console.WriteLine(BubbleSolverAdapterTests.RunManaged());
        Console.WriteLine(EngineSelfTests.RunManaged());
        if (Array.IndexOf(args, "--rhino") >= 0)
        {
            using (var core = new RhinoCore(new[] { "/NOSPLASH", "/NOTEMPLATE" }, WindowStyle.NoWindow))
            using (var doc = RhinoDoc.CreateHeadless(null))
            {
                Console.WriteLine(BubbleSolverAdapterTests.RunRhino(doc));
                Console.WriteLine(BubbleLayoutTests.Run(doc));
                Console.WriteLine(EngineSelfTests.RunGeometry());
                Console.WriteLine(EngineSelfTests.RunBake(doc));
            }
            return 0;
        }
        try
        {
            Console.WriteLine(EngineSelfTests.RunGeometry());
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine(
                "Geometry bake tests require Rhino 8. Run Test2DFloorplanEngine inside Rhino.");
        }
        return 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
