using System;
using System.IO;
using System.Runtime.InteropServices;
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
            Console.WriteLine(ExcelReaderTests.Run());
            Console.WriteLine(BubbleLayoutTests.RunCollision());
            Console.WriteLine(BubbleSolverAdapterTests.RunManaged());
            Console.WriteLine(EngineSelfTests.RunManaged());
            if (Array.IndexOf(args, "--rhino") >= 0)
            {
                var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rhino 8", "System");
                SetDllDirectory(system);
                Environment.SetEnvironmentVariable("PATH", system + ";" + Environment.GetEnvironmentVariable("PATH"));
                AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
                {
                    var name = new System.Reflection.AssemblyName(eventArgs.Name).Name + ".dll";
                    var file = Path.Combine(system, "netcore", name);
                    return File.Exists(file) ? System.Reflection.Assembly.LoadFrom(file) : null;
                };
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
        catch (Exception exception)
        {
            Console.Error.WriteLine("Test2DFloorplanEngine failed: " + exception);
            return 1;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
