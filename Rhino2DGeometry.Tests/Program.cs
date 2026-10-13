using System;
using SeniorDesign.Rhino2DGeometry;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Console.WriteLine(EngineSelfTests.RunManaged());
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
}
