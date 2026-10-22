using System;
using System.Collections.ObjectModel;
using Eto.Drawing;
using Eto.Forms;
using Rhino.UI;

namespace ProgramSyntaxPlugin
{
    public class SchemeResult
    {
        public string Name { get; set; } = string.Empty;
        public double Score { get; set; }
        public int Adjacency { get; set; }
        public int Travel { get; set; }
        public int Floors { get; set; }
    }

    [System.Runtime.InteropServices.Guid("B1C2D3E4-F5A6-4B8C-9D0E-1A2B3C4D5E6F")]
    public class ProgramSyntaxPanel : Panel, IPanel
    {
        private readonly ObservableCollection<SchemeResult> _schemes = new ObservableCollection<SchemeResult>();

        public ProgramSyntaxPanel()
        {
            var rootLayout = new DynamicLayout
            {
                Padding = new Padding(12),
                Spacing = new Size(0, 12) // Slightly more breathing room between sections
            };

            // 1. PANEL TITLE (Settings/Close buttons completely removed)
            rootLayout.AddRow(new Label { Text = "Program Syntax Model", Font = SystemFonts.Bold(13) });

            // Stretched Divider
            rootLayout.AddRow(new TableLayout { Rows = { new TableRow(new TableCell(new Panel { Height = 1, BackgroundColor = Colors.DarkGray }, true)) } });

            // 2. PROGRAM DATA
            rootLayout.AddRow(new Label { Text = "Program Data", Font = SystemFonts.Bold(10) });
            var lblFile = new Label { Text = "File: Program_Test.xlsm", VerticalAlignment = VerticalAlignment.Center };
            var btnBrowse = new Button { Text = "Browse Files..." };
            var btnRefresh = new Button { Text = "Refresh", Width = 70 };

            rootLayout.AddRow(new TableLayout
            {
                Spacing = new Size(4, 0),
                Rows = { new TableRow(new TableCell(lblFile, true), new TableCell(btnBrowse, false), new TableCell(btnRefresh, false)) }
            });

            rootLayout.AddRow(new TableLayout { Rows = { new TableRow(new TableCell(new Panel { Height = 1, BackgroundColor = Colors.DarkGray }, true)) } });

            // 3. SCORING WEIGHTS
            rootLayout.AddRow(new Label { Text = "Scoring Weights", Font = SystemFonts.Bold(10) });

            // Adjacency Slider (Stretches fully across)
            rootLayout.AddRow(new Label { Text = "Adjacency Satisfaction:", Font = SystemFonts.Default(9) });
            var sliderAdj = new Slider { MinValue = 0, MaxValue = 100, Value = 75 };
            var lblAdjVal = new Label { Text = "75%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sliderAdj.ValueChanged += (s, e) => lblAdjVal.Text = $"{sliderAdj.Value}%";
            rootLayout.AddRow(new TableLayout { Rows = { new TableRow(new TableCell(sliderAdj, true), new TableCell(lblAdjVal, false)) } });

            // Travel Slider
            rootLayout.AddRow(new Label { Text = "Travel Distance (Min. Flow):", Font = SystemFonts.Default(9) });
            var sliderTravel = new Slider { MinValue = 0, MaxValue = 100, Value = 35 };
            var lblTravelVal = new Label { Text = "35%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sliderTravel.ValueChanged += (s, e) => lblTravelVal.Text = $"{sliderTravel.Value}%";
            rootLayout.AddRow(new TableLayout { Rows = { new TableRow(new TableCell(sliderTravel, true), new TableCell(lblTravelVal, false)) } });

            // Daylight Slider
            rootLayout.AddRow(new Label { Text = "Daylight Perimeter:", Font = SystemFonts.Default(9) });
            var sliderDaylight = new Slider { MinValue = 0, MaxValue = 100, Value = 25 };
            var lblDaylightVal = new Label { Text = "25%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sliderDaylight.ValueChanged += (s, e) => lblDaylightVal.Text = $"{sliderDaylight.Value}%";
            rootLayout.AddRow(new TableLayout { Rows = { new TableRow(new TableCell(sliderDaylight, true), new TableCell(lblDaylightVal, false)) } });

            // NTG Slider
            rootLayout.AddRow(new Label { Text = "Net-To-Gross Target:", Font = SystemFonts.Default(9) });
            var sliderNtg = new Slider { MinValue = 0, MaxValue = 100, Value = 50 };
            var lblNtgVal = new Label { Text = "50%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sliderNtg.ValueChanged += (s, e) => lblNtgVal.Text = $"{sliderNtg.Value}%";
            rootLayout.AddRow(new TableLayout { Rows = { new TableRow(new TableCell(sliderNtg, true), new TableCell(lblNtgVal, false)) } });

            rootLayout.AddRow(new TableLayout { Rows = { new TableRow(new TableCell(new Panel { Height = 1, BackgroundColor = Colors.DarkGray }, true)) } });

            // 4. SOLVER ENGINE
            rootLayout.AddRow(new Label { Text = "Solver Engine", Font = SystemFonts.Bold(10) });

            var typologyDropdown = new DropDown();
            typologyDropdown.Items.Add("Double-Loaded Corridor");
            typologyDropdown.Items.Add("Racetrack Service Core");
            typologyDropdown.Items.Add("Courtyard Perimeter");
            typologyDropdown.Items.Add("Pod / Department Cluster");
            typologyDropdown.SelectedIndex = 0;

            rootLayout.AddRow(new TableLayout
            {
                Rows = { new TableRow(
                    new TableCell(new Label { Text = "Layout Type:", Width = 110, VerticalAlignment = VerticalAlignment.Center }, false),
                    new TableCell(typologyDropdown, true)
                )}
            });

            var iterationsInput = new NumericStepper { MinValue = 100, MaxValue = 50000, Value = 1000, Increment = 100 };
            rootLayout.AddRow(new TableLayout
            {
                Rows = { new TableRow(
                    new TableCell(new Label { Text = "Maximum Iterations:", Width = 110, VerticalAlignment = VerticalAlignment.Center }, false),
                    new TableCell(iterationsInput, true)
                )}
            });

            var btnRun = new Button { Text = "Run Solver ▶", Height = 28 };
            var btnStop = new Button { Text = "Stop ■", Height = 28 };
            rootLayout.AddRow(new TableLayout { Spacing = new Size(5, 0), Rows = { new TableRow(new TableCell(btnRun, true), new TableCell(btnStop, true)) } });

            var progressBar = new ProgressBar { MinValue = 0, MaxValue = 1000, Value = 700 };
            var lblIteration = new Label { Text = "Iteration: 700", Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            rootLayout.AddRow(new TableLayout { Spacing = new Size(5, 0), Rows = { new TableRow(new TableCell(progressBar, true), new TableCell(lblIteration, false)) } });

            rootLayout.AddRow(new TableLayout { Rows = { new TableRow(new TableCell(new Panel { Height = 1, BackgroundColor = Colors.DarkGray }, true)) } });

            // 5. SCHEME GALLERY (Taller container, larger rows)
            rootLayout.AddRow(new Label { Text = "Scheme Gallery", Font = SystemFonts.Bold(10) });

            var schemeGrid = new GridView
            {
                Height = 160,
                RowHeight = 28,
                ShowHeader = true,
                DataStore = _schemes
            };
            schemeGrid.Columns.Add(new GridColumn { HeaderText = "Scheme", DataCell = new TextBoxCell { Binding = Binding.Property<SchemeResult, string>(r => r.Name) }, Width = 95 });
            schemeGrid.Columns.Add(new GridColumn { HeaderText = "Score", DataCell = new TextBoxCell { Binding = Binding.Property<SchemeResult, string>(r => $"{r.Score:F1}") }, Width = 60 });
            schemeGrid.Columns.Add(new GridColumn { HeaderText = "Adjacency", DataCell = new TextBoxCell { Binding = Binding.Property<SchemeResult, string>(r => $"{r.Adjacency}%") }, Width = 75 });
            schemeGrid.Columns.Add(new GridColumn { HeaderText = "Travel", DataCell = new TextBoxCell { Binding = Binding.Property<SchemeResult, string>(r => $"{r.Travel}%") }, Width = 65 });

            _schemes.Add(new SchemeResult { Name = "Scheme #1", Score = 88.4, Adjacency = 92, Travel = 78, Floors = 2 });
            _schemes.Add(new SchemeResult { Name = "Scheme #2", Score = 82.1, Adjacency = 84, Travel = 81, Floors = 2 });
            _schemes.Add(new SchemeResult { Name = "Scheme #3", Score = 76.5, Adjacency = 79, Travel = 72, Floors = 2 });

            rootLayout.AddRow(schemeGrid);

            var btnPreview = new Button { Text = "👁 Preview", Height = 28 };
            var btnBake = new Button { Text = "📦 Bake to Rhino", Font = SystemFonts.Bold(9), Height = 28 };
            rootLayout.AddRow(new TableLayout { Spacing = new Size(5, 0), Rows = { new TableRow(new TableCell(btnPreview, true), new TableCell(btnBake, true)) } });

            rootLayout.AddRow(null); // Bottom spacer

            Content = new Scrollable
            {
                Content = rootLayout,
                Border = BorderType.None
            };
        }

        public void PanelShown(uint documentSerialNumber, ShowPanelReason reason) { }
        public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { }
        public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) { }
    }
}