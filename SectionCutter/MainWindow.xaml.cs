using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ETABSv1;
using SectionCutter.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;

namespace SectionCutter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private cPluginCallback _Plugin;
        private cSapModel _SapModel;

        private SectionCutJsonStore _jsonStore;

        public SCViewModel ViewModel { get; set; }

        // Results service (Step 3)
        private IEtabsResultsService _resultsService;

        private ISectionCutService _sectionCutService;

        // Cache “all-components” results so dropdown swaps don’t re-query ETABS
        private Dictionary<string, CutResultAll> _cutResultsAll = new(StringComparer.OrdinalIgnoreCase);

        private class CutResultAll
        {
            public string Name;
            public Point A;
            public Point B;
            public double Length;
            public double Shear;  // F1
            public double Axial;  // F2
            public double Moment; // M3
        }

        public MainWindow(cSapModel SapModel, cPluginCallback Plugin)
        {
            InitializeComponent();

            _SapModel = SapModel;
            _Plugin = Plugin;

            // JSON store (reads/writes SectionCut.json next to current ETABS model)
            _jsonStore = new SectionCutJsonStore(_SapModel);

            // Create the ETABS-backed SectionCut service and inject into the ViewModel
            ISectionCutService service = new EtabsSectionCutService(_SapModel, _jsonStore);
            _sectionCutService = new EtabsSectionCutService(_SapModel, _jsonStore);
            ViewModel = new SCViewModel(_SapModel, _sectionCutService);

            // Results service (Step 3)
            _resultsService = new EtabsResultsService(_SapModel);

            // 🔹 When Create Sections finishes → reload JSON + plot
            ViewModel.SectionCutsCreated += () =>
            {
                ReloadFromJsonAndPlot();
            };

            // 🔹 When user changes dropdown selection → reload plot for that prefix
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SCViewModel.SelectedPrefix))
                    ReloadSelectedPrefixAndPlot();

                // NEW: results view selection changes
                if (e.PropertyName == nameof(SCViewModel.SelectedResultsPrefix) ||
                    e.PropertyName == nameof(SCViewModel.SelectedLoadCase))
                {
                    ReloadResultsAndPopulateUI();
                }
            };

            this.DataContext = ViewModel;

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // ✅ Default view: Create Section Cuts
            SetSelected("cuts");
            try
            {
                ReloadFromJsonAndPlot();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load SectionCut.json:\n{ex.Message}",
                    "Load Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _Plugin?.Finish(0);
        }

        private void ReloadFromJsonAndPlot()
        {
            // Default: clear dropdown + plot (so "no JSON" shows nothing)
            ViewModel.SectionCutPrefixes.Clear();

            ViewModel.SelectedPrefix = null;
            ViewModel.SavedSectionCut = null;

            // Load root (list of saved sets)
            if (!_jsonStore.TryLoadRoot(out var root) || root?.Sets == null || root.Sets.Count == 0)
                return;

            // Clean and keep only valid sets (must have prefix)
            var validSets = root.Sets
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SectionCutPrefix))
                .ToList();

            if (validSets.Count == 0)
                return;

            // Populate dropdown with all prefixes
            var prefixes = validSets
                .Select(s => s.SectionCutPrefix.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var p in prefixes)
                ViewModel.SectionCutPrefixes.Add(p);

            // Decide which prefix to display:
            // 1) keep current SelectedPrefix if it exists
            // 2) otherwise choose most recently saved set
            string prefixToUse = null;

            if (!string.IsNullOrWhiteSpace(ViewModel.SelectedPrefix) &&
                prefixes.Any(p => string.Equals(p, ViewModel.SelectedPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                prefixToUse = ViewModel.SelectedPrefix;
            }
            else
            {
                prefixToUse = validSets
                    .OrderByDescending(s => s.SavedUtc)
                    .First()
                    .SectionCutPrefix;

                ViewModel.SelectedPrefix = prefixToUse;
            }

            // Load the selected set by prefix
            if (!_jsonStore.TryLoadByPrefix(prefixToUse, out var data) || data == null)
                return;

            // Restore last-used inputs (your existing logic)
            ViewModel.SectionCut.StartNodeId = data.StartNodeId;
            ViewModel.SectionCut.AreaIds = data.AreaIds ?? new List<string>();
            ViewModel.SectionCut.XVector = data.XVector;
            ViewModel.SectionCut.YVector = data.YVector;
            ViewModel.SectionCut.SectionCutPrefix = data.SectionCutPrefix;
            ViewModel.SectionCut.HeightAbove = data.HeightAbove;
            ViewModel.SectionCut.HeightBelow = data.HeightBelow;
            ViewModel.SectionCut.Units = string.IsNullOrWhiteSpace(data.Units) ? ViewModel.SectionCut.Units : data.Units;
            SyncUnitCheckBoxesFromUnits(ViewModel.SectionCut.Units);

            // Build SavedSectionCut VM for display + plot
            var vm = new SavedSectionCutVM
            {
                SectionCutPrefix = data.SectionCutPrefix,
                StartNodeId = data.StartNodeId,
                XVector = data.XVector,
                YVector = data.YVector,
                HeightAbove = data.HeightAbove,
                HeightBelow = data.HeightBelow,
                Units = data.Units
            };

            foreach (var id in data.AreaIds ?? new List<string>())
                vm.AreaIds.Add(id);

            foreach (var id in data.OpeningIds ?? new List<string>())
                vm.OpeningIds.Add(id);

            // Clear plot collections (probably empty anyway, but safe)
            vm.AreaPolygons.Clear();
            vm.OpeningPolygons.Clear();
            vm.Cuts.Clear();

            // Build plot geometry from ETABS (GLOBAL X/Y)
            BuildXYPolygonsFromEtabs(data, vm);

            if (data.CutSegmentsXY != null && data.CutSegmentsXY.Count > 0)
            {
                foreach (var seg in data.CutSegmentsXY)
                {
                    vm.Cuts.Add(new SectionCutPreviewControl.Segment(
                        new System.Windows.Point(seg.X1, seg.Y1),
                        new System.Windows.Point(seg.X2, seg.Y2)));
                }
            }
            else
            {
                // fallback for older JSON files
                BuildCutSegmentsXYFromEtabs(prefixToUse, vm);
            }

            // Push into VM (triggers UI updates)
            ViewModel.SavedSectionCut = vm;

            ViewModel.ValidateFields();
        }

        // ============================================================
        // Results: selectors -> grid -> plots (MVVM-bound like PreviewControl)
        // ============================================================

        private void ReloadResultsAndPopulateUI()
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedResultsPrefix))
            {
                ViewModel.ResultsRows.Clear();
                ClearResultsPlots();
                ClearResultsMeta();
                return;
            }

            if (ViewModel.SelectedLoadCase == null || string.IsNullOrWhiteSpace(ViewModel.SelectedLoadCase.Name))
            {
                ViewModel.ResultsRows.Clear();
                ClearResultsPlots();
                ClearResultsMeta();
                return;
            }

            string prefix = ViewModel.SelectedResultsPrefix;
            string loadCase = ViewModel.SelectedLoadCase.Name;

            if (!_jsonStore.TryLoadByPrefix(prefix, out var data) || data == null)
            {
                ViewModel.ResultsRows.Clear();
                ClearResultsPlots();
                ClearResultsMeta();   // <-- HERE
                return;
            }


            // ✅ IMPORTANT: set ETABS present units to match this prefix BEFORE querying geometry or tables
            SetEtabsUnitsFromString(data.Units);

            ViewModel.ResultsStartNodeId = data.StartNodeId;
            ViewModel.ResultsVectorLabel = $"X={data.XVector:0.###}, Y={data.YVector:0.###}";
            ViewModel.ResultsHeightLabel = $"Above={data.HeightAbove:0.###}, Below={data.HeightBelow:0.###}";
            ViewModel.ResultsUnitsLabel = (data.Units ?? "").Trim();


            // If we got here, we have data, so set meta labels:
            ViewModel.ResultsStartNodeId = data.StartNodeId;
            ViewModel.ResultsVectorLabel = $"X={data.XVector:0.###}, Y={data.YVector:0.###}";
            ViewModel.ResultsHeightLabel = $"Above={data.HeightAbove:0.###}, Below={data.HeightBelow:0.###}";
            ViewModel.ResultsUnitsLabel = (data.Units ?? "").Trim();




            // ---------- 1) Build polygons/openings from ETABS once ----------
            var tmpVm = new SavedSectionCutVM();
            tmpVm.AreaPolygons.Clear();
            tmpVm.OpeningPolygons.Clear();
            tmpVm.Cuts.Clear();

            BuildXYPolygonsFromEtabs(data, tmpVm);

            // Push polygons into VM collections (binding target)
            ViewModel.ResultsAreaPolygons.Clear();
            foreach (var pc in tmpVm.AreaPolygons)
                ViewModel.ResultsAreaPolygons.Add(pc);

            ViewModel.ResultsOpeningPolygons.Clear();
            foreach (var pc in tmpVm.OpeningPolygons)
                ViewModel.ResultsOpeningPolygons.Add(pc);

            // ---------- 2) Prefer JSON cut segments ----------
            var segs = data.CutSegmentsXY ?? new List<SectionCutCutSegmentXY>();

            var cutSegByName = segs
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
                .ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

            // TEMP: show cut geometry even if ETABS returns no forces
            PopulateCutLinesFromSegmentsOnly(cutSegByName);

            // Fallback: rebuild named cut segments from ETABS if JSON doesn’t have them
            if (cutSegByName.Count == 0)
            {
                cutSegByName = BuildNamedCutSegmentsXYFromEtabs(prefix);
            }

            // If still none, then truly nothing to plot
            if (cutSegByName.Count == 0)
            {
                ViewModel.ResultsRows.Clear();
                ClearResultsPlots();
                ClearResultsMeta();   // <-- ALSO HERE
                return;
            }


            // ---------- 3) Pull ETABS forces for this load case ----------
            var recs = _resultsService.GetSectionCutForces(loadCase);

            // ---------- 4) Cache “all components” results keyed by cut name ----------
            _cutResultsAll.Clear();

            foreach (var r in recs)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.SectionCutName)) continue;
                if (!cutSegByName.TryGetValue(r.SectionCutName, out var seg)) continue;

                var a = new Point(seg.X1, seg.Y1);
                var b = new Point(seg.X2, seg.Y2);
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                // double len = Math.Sqrt(dx * dx + dy * dy);

                double rawLen = Math.Sqrt(dx * dx + dy * dy);
                // Cuts are extended ±0.25 in U for full clipping; subtract 0.50 from reported length
                double userLen = Math.Max(0.0, rawLen - 0.5); // hide ±0.25 extension from user


                _cutResultsAll[r.SectionCutName] = new CutResultAll
                {
                    Name = r.SectionCutName,
                    A = a,
                    B = b,
                    Length = userLen,
                    Shear = r.F1,
                    Axial = r.F2,
                    Moment = r.M3
                };
            }

            // ---------- 5) Populate DataGrid ----------
            ViewModel.ResultsRows.Clear();
            foreach (var c in _cutResultsAll.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                ViewModel.ResultsRows.Add(new SectionCutResultRow
                {
                    Name = c.Name,
                    Shear = c.Shear,
                    Axial = c.Axial,
                    Moment = c.Moment,
                    Length = c.Length
                });
            }

            // ---------- 6) Populate plot-bound cut collections ----------
            UpdateDerivedResultColumns();
            ApplyResultsToPlots();
        }

        private void ClearResultsMeta()
        {
            ViewModel.ResultsStartNodeId = null;
            ViewModel.ResultsVectorLabel = null;
            ViewModel.ResultsHeightLabel = null;
            ViewModel.ResultsUnitsLabel = null;
        }

        private static void GetUnits(string unitsString, out string forceU, out string momentU, out string lengthU)
        {
            var u = (unitsString ?? "").ToLowerInvariant();

            bool metric = u.Contains("kn"); // "kN, m"
            forceU = metric ? "kN" : "kip";
            momentU = metric ? "kN*m" : "kip*ft";
            lengthU = metric ? "m" : "ft";
        }

        private static string GetValueUnitForComponent(string component, string forceU, string momentU)
        {
            switch ((component ?? "").Trim().ToLowerInvariant())
            {
                case "moment": return momentU;
                case "axial": return forceU;
                default: return forceU; // shear
            }
        }
        //TEMP: need to eventually remove.
        private void PopulateCutLinesFromSegmentsOnly(Dictionary<string, SectionCutCutSegmentXY> cutSegByName)
        {
            ViewModel.ResultsPlot1Cuts.Clear();
            ViewModel.ResultsPlot2Cuts.Clear();

            foreach (var kv in cutSegByName.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var seg = kv.Value;
                var item = new ResultCutPlotItem
                {
                    Name = seg.Name,
                    A = new Point(seg.X1, seg.Y1),
                    B = new Point(seg.X2, seg.Y2),
                    Length = Math.Sqrt(Math.Pow(seg.X2 - seg.X1, 2) + Math.Pow(seg.Y2 - seg.Y1, 2)),
                    Value = 0.0 // no results yet
                };

                ViewModel.ResultsPlot1Cuts.Add(item);
                ViewModel.ResultsPlot2Cuts.Add(item);
            }

            if (ResultsPlot1 != null) ResultsPlot1.FitToContent();
            if (ResultsPlot2 != null) ResultsPlot2.FitToContent();
        }

        private ETABSv1.eUnits GetEtabsUnitsFromString(string units)
        {
            if (string.IsNullOrWhiteSpace(units))
                return ETABSv1.eUnits.kip_ft_F;

            var u = units.Trim().ToLowerInvariant();

            if (u.Contains("kip") && u.Contains("ft"))
                return ETABSv1.eUnits.kip_ft_F;

            if (u.Contains("kn") && (u.Contains("m") || u.Contains("meter")))
                return ETABSv1.eUnits.kN_m_C;

            return ETABSv1.eUnits.kip_ft_F;
        }

        private void SetEtabsUnitsFromString(string units)
        {
            try
            {
                _SapModel?.SetPresentUnits(GetEtabsUnitsFromString(units));
            }
            catch
            {
                // swallow – worst case your units remain as-is
            }
        }

        private void ApplyResultsToPlots()
        {
            // Build cut collections per plot based on VM component selection
            var comp1 = ViewModel.Plot1Component ?? "Shear";
            var comp2 = ViewModel.Plot2Component ?? "Moment";

            var plot1Cuts = BuildResultCutsForComponent(comp1);
            var plot2Cuts = BuildResultCutsForComponent(comp2);

            // Units context for results (driven by JSON prefix selection)
            GetUnits(ViewModel.ResultsUnitsLabel, out var forceU, out var momentU, out var lengthU);

            var v1U = GetValueUnitForComponent(comp1, forceU, momentU);
            var v2U = GetValueUnitForComponent(comp2, forceU, momentU);

            // Plot labels + tooltip units
            if (ResultsPlot1 != null)
            {
                ResultsPlot1.DiagramLabel = $"{comp1} ({v1U})";
                ResultsPlot1.ValueUnits = v1U;
                ResultsPlot1.LengthUnits = lengthU;
            }
            if (ResultsPlot2 != null)
            {
                ResultsPlot2.DiagramLabel = $"{comp2} ({v2U})";
                ResultsPlot2.ValueUnits = v2U;
                ResultsPlot2.LengthUnits = lengthU;
            }

            // DataGrid column headers with units
            if (ResultsDataGrid != null && ResultsDataGrid.Columns.Count >= 5)
            {
                ResultsDataGrid.Columns[1].Header = $"Shear ({forceU})";
                ResultsDataGrid.Columns[2].Header = $"Moment ({momentU})";
                ResultsDataGrid.Columns[3].Header = $"Axial ({forceU})";
                ResultsDataGrid.Columns[4].Header = $"Length ({lengthU})";
            }


            // Push into VM collections (binding target)
            ViewModel.ResultsPlot1Cuts.Clear();
            foreach (var c in plot1Cuts)
                ViewModel.ResultsPlot1Cuts.Add(c);

            ViewModel.ResultsPlot2Cuts.Clear();
            foreach (var c in plot2Cuts)
                ViewModel.ResultsPlot2Cuts.Add(c);

            // Auto-scale diagram length (control property is fine to set from code-behind)
            if (ResultsPlot1 != null)
            {
                var bounds1 = ComputeWorldBounds(ViewModel.ResultsAreaPolygons, ViewModel.ResultsOpeningPolygons, ViewModel.ResultsPlot1Cuts);
                ResultsPlot1.ValueScale = ComputeValueScale(bounds1, ViewModel.ResultsPlot1Cuts.Select(c => c.Value));
                ResultsPlot1.FitToContent();
            }
            
            if (ResultsPlot2 != null)
            {
                var bounds2 = ComputeWorldBounds(ViewModel.ResultsAreaPolygons, ViewModel.ResultsOpeningPolygons, ViewModel.ResultsPlot2Cuts);
                ResultsPlot2.ValueScale = ComputeValueScale(bounds2, ViewModel.ResultsPlot2Cuts.Select(c => c.Value));
                ResultsPlot2.FitToContent();
            }
        }

        private List<ResultCutPlotItem> BuildResultCutsForComponent(string component)
        {
            double pick(CutResultAll c)
            {
                switch ((component ?? "").Trim().ToLowerInvariant())
                {
                    case "moment": return c.Moment;
                    case "axial": return c.Axial;
                    default: return c.Shear;
                }
            }

            var list = new List<ResultCutPlotItem>();

            foreach (var c in _cutResultsAll.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(new ResultCutPlotItem
                {
                    Name = c.Name,
                    A = c.A,
                    B = c.B,
                    Length = c.Length,
                    Value = pick(c)
                });
            }

            return list;
        }

        private static Rect ComputeWorldBounds(
            ObservableCollection<PointCollection> areas,
            ObservableCollection<PointCollection> openings,
            IEnumerable<ResultCutPlotItem> cuts)
        {
            Rect r = Rect.Empty;

            void UnionPc(PointCollection pc)
            {
                if (pc == null) return;
                foreach (var p in pc)
                {
                    if (r.IsEmpty) r = new Rect(p, new Size(0, 0));
                    else r.Union(p);
                }
            }

            if (areas != null) foreach (var pc in areas) UnionPc(pc);
            if (openings != null) foreach (var pc in openings) UnionPc(pc);

            if (cuts != null)
            {
                foreach (var c in cuts)
                {
                    if (c == null) continue;
                    if (r.IsEmpty) r = new Rect(c.A, new Size(0, 0));
                    r.Union(c.A);
                    r.Union(c.B);
                }
            }

            return r;
        }

        private static double ComputeValueScale(Rect worldBounds, IEnumerable<double> values)
        {
            double maxAbs = values.Select(v => Math.Abs(v)).DefaultIfEmpty(0.0).Max();
            if (maxAbs < 1e-9) return 1.0;

            // target diagram length ~20% of the smaller content dimension
            double minDim = Math.Max(1e-6, Math.Min(worldBounds.Width, worldBounds.Height));
            double target = 0.20 * minDim;

            return target / maxAbs;
        }

        private void ClearResultsPlots()
        {
            // Clear VM-bound plot data
            ViewModel.ResultsAreaPolygons.Clear();
            ViewModel.ResultsOpeningPolygons.Clear();
            ViewModel.ResultsPlot1Cuts.Clear();
            ViewModel.ResultsPlot2Cuts.Clear();

            // Clear selection highlight (control-side)
            if (ResultsPlot1 != null) ResultsPlot1.SelectedCutName = null;
            if (ResultsPlot2 != null) ResultsPlot2.SelectedCutName = null;
        }

        // XAML event handlers from Step 1
        private void PlotComponent_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ViewModel == null) return;

            if (sender == Plot1ComponentCombo)
            {
                var item = Plot1ComponentCombo.SelectedItem as ComboBoxItem;
                ViewModel.Plot1Component = item?.Content?.ToString() ?? "Shear";
            }
            else if (sender == Plot2ComponentCombo)
            {
                var item = Plot2ComponentCombo.SelectedItem as ComboBoxItem;
                ViewModel.Plot2Component = item?.Content?.ToString() ?? "Moment";
            }

            // Rebuild plot cuts from cached results (no ETABS query)
            ApplyResultsToPlots();
        }

        private void ResultsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ResultsDataGrid.SelectedItem is SectionCutResultRow row &&
                !string.IsNullOrWhiteSpace(row.Name))
            {
                ResultsPlot1.SelectedCutName = row.Name;
                ResultsPlot2.SelectedCutName = row.Name;

                ResultsPlot1.CenterOnCut(row.Name);
                ResultsPlot2.CenterOnCut(row.Name);
            }
            else
            {
                ResultsPlot1.SelectedCutName = null;
                ResultsPlot2.SelectedCutName = null;
            }
        }

        // ============================================================
        // Existing UI wiring / create-cuts view
        // ============================================================

        private void ToggleSidebarBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SidebarColumn.Width.Value > 10)
            {
                SidebarColumn.Width = new GridLength(0); // Collapse
            }
            else
            {
                SidebarColumn.Width = new GridLength(200); // Expand
            }
        }

        private void ReloadSelectedPrefixAndPlot()
        {
            var prefix = ViewModel.SelectedPrefix;

            // If nothing selected, clear display + plot
            if (string.IsNullOrWhiteSpace(prefix))
            {
                ViewModel.SavedSectionCut = null;
                return;
            }

            // Load only the selected set from the list-based JSON
            if (!_jsonStore.TryLoadByPrefix(prefix, out var data) || data == null)
            {
                ViewModel.SavedSectionCut = null;
                return;
            }

            // ✅ Set ETABS units before fetching area point coords / table coords
            SetEtabsUnitsFromString(data.Units);

            // Restore the input fields to match the selected saved set
            ViewModel.SectionCut.StartNodeId = data.StartNodeId;
            ViewModel.SectionCut.AreaIds = data.AreaIds ?? new List<string>();
            ViewModel.SectionCut.XVector = data.XVector;
            ViewModel.SectionCut.YVector = data.YVector;
            ViewModel.SectionCut.SectionCutPrefix = data.SectionCutPrefix;
            ViewModel.SectionCut.HeightAbove = data.HeightAbove;
            ViewModel.SectionCut.HeightBelow = data.HeightBelow;
            ViewModel.SectionCut.Units = string.IsNullOrWhiteSpace(data.Units) ? ViewModel.SectionCut.Units : data.Units;
            SyncUnitCheckBoxesFromUnits(ViewModel.SectionCut.Units);

            // Build SavedSectionCut VM for display + plot
            var vm = new SavedSectionCutVM
            {
                SectionCutPrefix = data.SectionCutPrefix,
                StartNodeId = data.StartNodeId,
                XVector = data.XVector,
                YVector = data.YVector,
                HeightAbove = data.HeightAbove,
                HeightBelow = data.HeightBelow,
                Units = data.Units
            };

            foreach (var id in data.AreaIds ?? new List<string>())
                vm.AreaIds.Add(id);

            foreach (var id in data.OpeningIds ?? new List<string>())
                vm.OpeningIds.Add(id);

            vm.AreaPolygons.Clear();
            vm.OpeningPolygons.Clear();
            vm.Cuts.Clear();

            BuildXYPolygonsFromEtabs(data, vm);
            BuildCutSegmentsXYFromEtabs(prefix, vm);

            ViewModel.SavedSectionCut = vm;

            ViewModel.ValidateFields();
        }

        private void UpdateDerivedResultColumns()
        {
            if (ViewModel == null) return;

            // Units
            GetUnits(ViewModel.ResultsUnitsLabel, out var forceU, out var momentU, out var lengthU);

            // Percent (1-100)
            double p = ViewModel.ChordDepthPercent;
            double depthFrac = p / 100.0;
            if (depthFrac <= 0) depthFrac = 1.0;

            foreach (var r in ViewModel.ResultsRows)
            {
                if (r == null) continue;

                // Unit Shear = Shear / Length
                if (Math.Abs(r.Length) > 1e-9)
                    r.UnitShear = r.Shear / r.Length;
                else
                    r.UnitShear = 0.0;

                // Chord Force Est = Moment / (Length * depthFrac)
                // Units: (kip*ft)/(ft)=kip, (kN*m)/(m)=kN
                if (Math.Abs(r.Length * depthFrac) > 1e-9)
                    r.ChordForce = r.Moment / (r.Length * depthFrac);
                else
                    r.ChordForce = 0.0;
            }

            // Refresh the grid view (since rows are POCOs)
            ResultsDataGrid?.Items.Refresh();

            // Update headers to include correct units
            UpdateResultsGridHeaders(forceU, momentU, lengthU);
        }

        private void UpdateResultsGridHeaders(string forceU, string momentU, string lengthU)
        {
            // Unit Shear units = force/length
            string unitShearU = $"{forceU}/{lengthU}";
            string chordU = forceU; // chord force estimate is force

            // Update column headers (adjust indexes if your order differs)
            if (ResultsDataGrid == null || ResultsDataGrid.Columns.Count < 7) return;

            // Example assumes columns:
            // 0 Name, 1 Shear, 2 UnitShear, 3 Moment, 4 Axial, 5 ChordForce, 6 Length
            ResultsDataGrid.Columns[1].Header = $"Shear ({forceU})";
            ResultsDataGrid.Columns[2].Header = $"Moment ({momentU})";
            ResultsDataGrid.Columns[3].Header = $"Axial ({forceU})";
            ResultsDataGrid.Columns[4].Header = $"Length ({lengthU})";
            ResultsDataGrid.Columns[5].Header = $"Unit Shear ({unitShearU})";
            ResultsDataGrid.Columns[6].Header = $"Chord Force Est. ({chordU})";
        }

        private void ChordPercentTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // allow digits and decimal point only
            e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '.');
        }

        private void ReloadResultsPrefixes()
        {
            if (ViewModel == null) return;

            ViewModel.ResultsPrefixes.Clear();

            // Whatever method you already have that returns all saved set prefixes
            // (examples: GetAllPrefixes(), GetSectionCutPrefixes(), LoadAllPrefixes(), etc.)
            var prefixes = _jsonStore.GetAllPrefixes();

            foreach (var p in prefixes)
                ViewModel.ResultsPrefixes.Add(p);

            // If current selection no longer exists, clear it
            if (!string.IsNullOrWhiteSpace(ViewModel.SelectedResultsPrefix) &&
                !prefixes.Any(x => string.Equals(x, ViewModel.SelectedResultsPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                ViewModel.SelectedResultsPrefix = null;
            }
        }

        private void DeleteSelectedSet_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            var prefix = ViewModel.SelectedPrefix;
            if (string.IsNullOrWhiteSpace(prefix))
            {
                MessageBox.Show("Select a section cut set to delete first.");
                return;
            }

            var confirm = MessageBox.Show(
                $"Delete section cut set \"{prefix}\"?\n\n" +
                "- Removes it from SectionCut.json\n" +
                "- Deletes all ETABS section cuts with names starting with this prefix",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;

            try
            {
                // 1) Remove from ETABS definitions
                _sectionCutService.DeleteEtabsSectionCutsByPrefix(prefix);

                // 2) Remove from JSON
                _jsonStore.DeleteByPrefix(prefix);

                // 3) Refresh UI lists
                ReloadFromJsonAndPlot();          // refresh "Created Section Cut Set" pane
                ReloadResultsPrefixes();          // refresh results prefixes dropdown if you have this method
                ViewModel.SelectedPrefix = null;

                // Optional: clear plots/results if they referenced this prefix
                if (string.Equals(ViewModel.SelectedResultsPrefix, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    ViewModel.SelectedResultsPrefix = null;
                    ViewModel.ResultsRows.Clear();
                    ClearResultsPlots();
                    ClearResultsMeta();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to delete set \"{prefix}\":\n{ex.Message}",
                    "Delete Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ChordPercentTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ChordPercentTextBox == null || ViewModel == null) return;

            if (!double.TryParse(ChordPercentTextBox.Text, out var v))
                v = ViewModel.ChordDepthPercent;

            // setter clamps 1..100
            ViewModel.ChordDepthPercent = v;

            // make textbox reflect clamped formatted value
            ChordPercentTextBox.Text = ViewModel.ChordDepthPercent.ToString("0.##");

            UpdateDerivedResultColumns();
        }

        private void SyncUnitCheckBoxesFromUnits(string units)
        {
            if (KipFtCheckBox == null || KnMCheckBox == null)
                return;

            var u = (units ?? "").Trim().ToLowerInvariant();
            if (u.Contains("kn") && u.Contains("m"))
            {
                KnMCheckBox.IsChecked = true;
                KipFtCheckBox.IsChecked = false;
            }
            else
            {
                KipFtCheckBox.IsChecked = true;
                KnMCheckBox.IsChecked = false;
            }
        }

        private void BuildXYPolygonsFromEtabs(SectionCutJsonData data, SectionCutter.ViewModels.SavedSectionCutVM vm)
        {
            // Decide which of the saved area IDs are openings
            var openingSet = new HashSet<string>(data.OpeningIds ?? new List<string>());

            foreach (var areaId in data.AreaIds ?? new List<string>())
            {
                int n = 0;
                string[] ptNames = null;

                int r = _SapModel.AreaObj.GetPoints(areaId, ref n, ref ptNames);
                if (r != 0 || ptNames == null || n < 3) continue;

                var poly = new PointCollection();

                for (int i = 0; i < n; i++)
                {
                    double x = 0, y = 0, z = 0;
                    _SapModel.PointObj.GetCoordCartesian(ptNames[i], ref x, ref y, ref z);

                    // ✅ GLOBAL X/Y
                    poly.Add(new Point(x, y));
                }

                if (poly.Count >= 3)
                {
                    if (openingSet.Contains(areaId))
                        vm.OpeningPolygons.Add(poly);
                    else
                        vm.AreaPolygons.Add(poly);
                }
            }
        }

        private void BuildCutSegmentsXYFromEtabs(string prefix, SectionCutter.ViewModels.SavedSectionCutVM vm)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return;

            string tableKey = "Section Cut Definitions";
            string[] fieldKeyList = null;
            string groupName = "All";
            int tableVersion = 1;
            string[] fieldKeysIncluded = null;
            int numberRecords = 0;
            string[] tableData = null;

            int ret = _SapModel.DatabaseTables.GetTableForDisplayArray(
                tableKey,
                ref fieldKeyList,
                groupName,
                ref tableVersion,
                ref fieldKeysIncluded,
                ref numberRecords,
                ref tableData);

            if (ret != 0 || fieldKeysIncluded == null || tableData == null || numberRecords <= 0)
                return;

            int nf = fieldKeysIncluded.Length;

            int idxName = Array.IndexOf(fieldKeysIncluded, "Name");
            int idxQuadNum = Array.IndexOf(fieldKeysIncluded, "QuadNum");
            int idxPointNum = Array.IndexOf(fieldKeysIncluded, "PointNum");
            int idxX = Array.IndexOf(fieldKeysIncluded, "QuadX");
            int idxY = Array.IndexOf(fieldKeysIncluded, "QuadY");

            if (idxName < 0 || idxQuadNum < 0 || idxPointNum < 0 || idxX < 0 || idxY < 0)
                return;

            // Collect endpoints per cut name (use point 1 and point 4 of quad 1)
            var p1 = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            var p4 = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);

            for (int r2 = 0; r2 < numberRecords; r2++)
            {
                int b = r2 * nf;

                string name = tableData[b + idxName];
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string quadNum = tableData[b + idxQuadNum];
                if (quadNum != "1") // we only need the first quad
                    continue;

                string pointNum = tableData[b + idxPointNum];

                if (!double.TryParse(tableData[b + idxX], out double x)) continue;
                if (!double.TryParse(tableData[b + idxY], out double y)) continue;

                var pt = new Point(x, y);

                if (pointNum == "1") p1[name] = pt;
                if (pointNum == "4") p4[name] = pt;
            }

            foreach (var kv in p1)
            {
                if (p4.TryGetValue(kv.Key, out var end))
                {
                    vm.Cuts.Add(new SectionCutPreviewControl.Segment(kv.Value, end));
                }
            }
        }

        private Dictionary<string, SectionCutCutSegmentXY> BuildNamedCutSegmentsXYFromEtabs(string prefix)
        {
            var dict = new Dictionary<string, SectionCutCutSegmentXY>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(prefix))
                return dict;

            const string tableKey = "Section Cut Definitions";

            string[] fieldKeyList = null;
            string groupName = "All";
            int tableVersion = 1;
            string[] fieldKeysIncluded = null;
            int numberRecords = 0;
            string[] tableData = null;

            int ret = _SapModel.DatabaseTables.GetTableForDisplayArray(
                tableKey,
                ref fieldKeyList,
                groupName,
                ref tableVersion,
                ref fieldKeysIncluded,
                ref numberRecords,
                ref tableData);

            if (ret != 0 || fieldKeysIncluded == null || tableData == null || numberRecords <= 0)
                return dict;

            int nf = fieldKeysIncluded.Length;

            int idxName = Array.IndexOf(fieldKeysIncluded, "Name");
            int idxQuadNum = Array.IndexOf(fieldKeysIncluded, "QuadNum");
            int idxPointNum = Array.IndexOf(fieldKeysIncluded, "PointNum");
            int idxX = Array.IndexOf(fieldKeysIncluded, "QuadX");
            int idxY = Array.IndexOf(fieldKeysIncluded, "QuadY");

            if (idxName < 0 || idxQuadNum < 0 || idxPointNum < 0 || idxX < 0 || idxY < 0)
                return dict;

            var p1 = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            var p4 = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);

            for (int r2 = 0; r2 < numberRecords; r2++)
            {
                int b = r2 * nf;

                string name = tableData[b + idxName];
                if (string.IsNullOrWhiteSpace(name) ||
                    !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (tableData[b + idxQuadNum] != "1")
                    continue;

                string pointNum = tableData[b + idxPointNum];

                if (!double.TryParse(tableData[b + idxX], out double x)) continue;
                if (!double.TryParse(tableData[b + idxY], out double y)) continue;

                var pt = new Point(x, y);
                if (pointNum == "1") p1[name] = pt;
                if (pointNum == "4") p4[name] = pt;
            }

            foreach (var kv in p1)
            {
                if (p4.TryGetValue(kv.Key, out var end))
                {
                    dict[kv.Key] = new SectionCutCutSegmentXY
                    {
                        Name = kv.Key,
                        X1 = kv.Value.X,
                        Y1 = kv.Value.Y,
                        X2 = end.X,
                        Y2 = end.Y
                    };
                }
            }

            return dict;
        }

        private void SetSelected(string selected)
        {
            // Reset all button visuals
            SectionCutsSidebarBtn.Background = Brushes.Black;
            ReviewResultsSidebarBtn.Background = Brushes.Black;
            SectionCutsIconBorder.BorderBrush = Brushes.Transparent;
            ReviewResultsIconBorder.BorderBrush = Brushes.Transparent;

            // Reset content visibility
            CreateSectionCutsGrid.Visibility = Visibility.Collapsed;
            ReviewResultsGrid.Visibility = Visibility.Collapsed;

            // Activate selected
            if (selected == "cuts")
            {
                SectionCutsSidebarBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff8c69"));
                SectionCutsIconBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff8c69"));
                CreateSectionCutsGrid.Visibility = Visibility.Visible;
            }
            else if (selected == "results")
            {
                ReviewResultsSidebarBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff8c69"));
                ReviewResultsIconBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff8c69"));
                ReviewResultsGrid.Visibility = Visibility.Visible;
            }
        }

        private void CreateSectionCut_Click(object sender, RoutedEventArgs e)
        {
            SetSelected("cuts");
        }

        private void ReviewResults_Click(object sender, RoutedEventArgs e)
        {
            SetSelected("results");
            EnsureResultsSelectorsLoaded();
        }

        private void EnsureResultsSelectorsLoaded()
        {
            // Prefixes from JSON
            ViewModel.ResultsPrefixes.Clear();
            foreach (var p in _jsonStore.GetAllPrefixes())
                ViewModel.ResultsPrefixes.Add(p);

            // Default SelectedResultsPrefix
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedResultsPrefix))
            {
                // Prefer whatever you last had selected in the Create view if present
                if (!string.IsNullOrWhiteSpace(ViewModel.SelectedPrefix) &&
                    ViewModel.ResultsPrefixes.Any(x => string.Equals(x, ViewModel.SelectedPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    ViewModel.SelectedResultsPrefix = ViewModel.SelectedPrefix;
                }
                else if (ViewModel.ResultsPrefixes.Count > 0)
                {
                    ViewModel.SelectedResultsPrefix = ViewModel.ResultsPrefixes[0];
                }
            }

            // Load cases
            ViewModel.LoadCases.Clear();
            foreach (var lc in _resultsService.GetLoadCasesWithStatus().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                ViewModel.LoadCases.Add(lc);

            // Default load case: pick first HasResults == true, else first
            if (ViewModel.SelectedLoadCase == null)
            {
                ViewModel.SelectedLoadCase =
                    ViewModel.LoadCases.FirstOrDefault(x => x.HasResults) ??
                    ViewModel.LoadCases.FirstOrDefault();
            }

            // Populate grid + plots immediately if both selections exist
            ReloadResultsAndPopulateUI();
        }

        // ============================================================
        // Input validation + unit toggles (unchanged)
        // ============================================================

        private void DecimalInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Allow digits and one decimal, non-negative
            TextBox textBox = sender as TextBox;
            string proposed = GetProposedText(textBox, e.Text);

            if (!decimal.TryParse(proposed, out decimal result) || result < 0)
            {
                e.Handled = true;
            }
        }

        private void IntegerInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Only allow integers 1–1000
            if (!int.TryParse(((TextBox)sender).Text + e.Text, out int val))
            {
                e.Handled = true;
            }
            else
            {
                e.Handled = val < 1 || val > 1000;
            }
        }

        private void PositiveIntegerInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            TextBox textBox = sender as TextBox;
            string proposedText = GetProposedText(textBox, e.Text);

            // Only allow digits
            bool isDigitsOnly = Regex.IsMatch(e.Text, @"^\d+$");

            // Try parse as integer and check bounds
            if (isDigitsOnly && int.TryParse(proposedText, out int val))
            {
                e.Handled = !(val >= 1 && val <= 1000);
            }
            else
            {
                e.Handled = true;
            }
        }

        private string GetProposedText(TextBox textBox, string input)
        {
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionLength;

            return currentText.Remove(selectionStart, selectionLength)
                              .Insert(selectionStart, input);
        }

        private void KipFtCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (KnMCheckBox != null)
                KnMCheckBox.IsChecked = false;

            if (ViewModel?.SectionCut != null)
            {
                ViewModel.SectionCut.Units = "kip, ft";
                ViewModel.ValidateFields();
            }
        }

        private void KnMCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (KipFtCheckBox != null)
                KipFtCheckBox.IsChecked = false;

            if (ViewModel?.SectionCut != null)
            {
                ViewModel.SectionCut.Units = "kN, m";
                ViewModel.ValidateFields();
            }
        }
    }
}
