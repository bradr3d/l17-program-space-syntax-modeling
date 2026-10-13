using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    internal sealed class GenerationSettings
    {
        public double PlateWidth = 260.0;
        public double PlateLength = 200.0;
        public double FloorEfficiency = 0.70;
        public int MaxFloors = 20;
        public int ClusterCount = 6;
        public double ProximityWeight = 1.0;
        public double AdjacencyWeight = 2.5;
        public double DepartmentWeight = 1.0;
        public double RoomDepth = 28.0;
        public double MaxAspect = 2.5;
        public double MaxCorridorLength = 75.0;
        public double RoomGap = 0.5;
        public double BranchGap = 6.0;
        public double TrunkGap = 8.0;
        public double BranchWidth = 3.0;
        public double TrunkBaseWidth = 3.0;
        public double TrunkStep = 0.5;
        public int TrunkThreshold = 4;
        public double TrunkMaxWidth = 8.0;
        public double DefaultDoorWidth = 3.0;
        public double FloorGap = 30.0;
        public int Seed = 42;
        public Point3d Origin = Point3d.Origin;
        public double UnitScale = 1.0;

        public double Model(double inputUnits)
        {
            return inputUnits * UnitScale;
        }

        public double TrunkRibbonWidth(int roomsServed)
        {
            return Math.Min(Model(TrunkMaxWidth),
                Model(TrunkBaseWidth) +
                Math.Max(0, roomsServed - TrunkThreshold) * Model(TrunkStep));
        }
    }

    internal sealed class BubbleRoom
    {
        public Guid SourceId;
        public string Uid;
        public string Label;
        public string Department;
        public double Area;
        public int FloorPreference;
        public bool Shared;
        public string AdjacentTo;
        public double DoorWidth;
        public Point3d BubbleCenter;
        public double BubbleRadius;
        public Color Color;
    }

    internal sealed class RoomPlacement
    {
        public BubbleRoom Room;
        public int Floor;
        public int Cluster;
        public Rectangle3d Rectangle;
        public int AccessEdge;
    }

    internal sealed class CorridorSegment
    {
        public int Floor;
        public Line Line;
        public bool IsTrunk;
        public int RoomsServed;
        public double Width;
        public Curve Ribbon;
    }

    internal sealed class DoorPlacement
    {
        public BubbleRoom Room;
        public int Floor;
        public bool IsDirect;
        public int Edge;
        public Point3d Hinge;
        public Point3d ClosedEnd;
        public Point3d OpenEnd;
        public Arc Swing;
        public double Width;
    }

    internal sealed class DiagnosticCurve
    {
        public int Floor;
        public string Kind;
        public Curve Curve;
    }

    internal sealed class FloorplanResult
    {
        public readonly List<RoomPlacement> Rooms = new List<RoomPlacement>();
        public readonly List<CorridorSegment> Corridors = new List<CorridorSegment>();
        public readonly List<DoorPlacement> Doors = new List<DoorPlacement>();
        public readonly List<DiagnosticCurve> Diagnostics = new List<DiagnosticCurve>();
        public readonly List<string> Warnings = new List<string>();
        public int FloorCount;
    }

    internal static class FloorplanPipeline
    {
        public static FloorplanResult Generate(
            IList<BubbleRoom> rooms, GenerationSettings settings, double tolerance)
        {
            var result = LayoutEngine.Generate(rooms, settings);
            CirculationEngine.Build(result, settings, tolerance);
            return result;
        }
    }
}
