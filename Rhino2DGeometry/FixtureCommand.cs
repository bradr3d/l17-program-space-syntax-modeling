using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace SeniorDesign.Rhino2DGeometry
{
    public sealed class CreateFloorplanFixtureCommand : Command
    {
        public override string EnglishName { get { return "Create2DFloorplanFixture"; } }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var scale = RhinoMath.UnitScale(UnitSystem.Feet, doc.ModelUnitSystem);
            if (!RhinoMath.IsValidDouble(scale) || scale <= 0)
                scale = 1.0;
            var data = new[]
            {
                new Fixture("A#0", "Classroom A", "Academic", 850, 0, 0, "", false),
                new Fixture("A.R#0", "Restroom A", "Support", 80, 17, 0, "A#0", true),
                new Fixture("B#0", "Classroom B", "Academic", 850, 35, 2, "A#0", false),
                new Fixture("C#0", "Science Lab", "Science", 1200, 65, 5, "B#0", false),
                new Fixture("C.S#0", "Lab Storage", "Support", 120, 82, 5, "C#0", true),
                new Fixture("D#0", "Administration", "Admin", 600, 10, 35, "A#0", false),
                new Fixture("E#0", "Library", "Academic", 1800, 45, 38, "D#0", false),
                new Fixture("F#0", "Multipurpose", "Community", 2400, 78, 42, "E#0", false)
            };

            var ids = new List<Guid>();
            foreach (var item in data)
            {
                var radius = Math.Sqrt(item.Area / Math.PI) * scale;
                var circle = new Circle(
                    new Point3d(item.X * scale, item.Y * scale, 0), radius);
                var attributes = new ObjectAttributes
                {
                    Name = item.Label,
                    ColorSource = ObjectColorSource.ColorFromObject,
                    ObjectColor = DepartmentColor(item.Department)
                };
                attributes.SetUserString("UID", item.Uid);
                attributes.SetUserString("Area", item.Area.ToString());
                attributes.SetUserString("Label", item.Label);
                attributes.SetUserString("Dept", item.Department);
                attributes.SetUserString("FloorPref", "1");
                attributes.SetUserString("Shared", item.Shared.ToString());
                attributes.SetUserString("AdjacentTo", item.AdjacentTo);
                attributes.SetUserString("DoorWidth", "3");
                var id = doc.Objects.AddCircle(circle, attributes);
                if (id != Guid.Empty)
                    ids.Add(id);
            }
            if (ids.Count > 0)
                doc.Groups.Add("2D Floorplan Fixture", ids);
            doc.Views.Redraw();
            RhinoApp.WriteLine("Created " + ids.Count +
                               " fixture bubbles. Run Generate2DFloorplan and select them.");
            return ids.Count == data.Length ? Result.Success : Result.Failure;
        }

        private static Color DepartmentColor(string department)
        {
            if (department == "Academic") return Color.CornflowerBlue;
            if (department == "Science") return Color.MediumSeaGreen;
            if (department == "Admin") return Color.Goldenrod;
            if (department == "Community") return Color.MediumPurple;
            return Color.LightGray;
        }

        private sealed class Fixture
        {
            public Fixture(string uid, string label, string department, double area,
                double x, double y, string adjacentTo, bool shared)
            {
                Uid = uid;
                Label = label;
                Department = department;
                Area = area;
                X = x;
                Y = y;
                AdjacentTo = adjacentTo;
                Shared = shared;
            }

            public readonly string Uid;
            public readonly string Label;
            public readonly string Department;
            public readonly double Area;
            public readonly double X;
            public readonly double Y;
            public readonly string AdjacentTo;
            public readonly bool Shared;
        }
    }
}
