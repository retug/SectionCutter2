using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ETABSv1;
using SectionCutter.ViewModels;
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

        public MainWindow(cSapModel SapModel, cPluginCallback Plugin)
        {
            InitializeComponent();

            _SapModel = SapModel;
            _Plugin = Plugin;

            // JSON store (reads/writes SectionCut.json next to current ETABS model)
            _jsonStore = new SectionCutJsonStore(_SapModel);

            // Create the ETABS-backed SectionCut service and inject into the ViewModel
            ISectionCutService service = new EtabsSectionCutService(_SapModel, _jsonStore);
            ViewModel = new SCViewModel(_SapModel, service);

            // 🔹 When Create Sections finishes → reload JSON + plot
            ViewModel.SectionCutsCreated += () =>
            {
                ReloadFromJsonAndPlot();
            };

            // 🔹 THIS IS THE LINE YOU ASKED ABOUT
            // 🔹 When user changes dropdown selection → reload plot for that prefix
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SCViewModel.SelectedPrefix))
                    ReloadSelectedPrefixAndPlot();
            };


            this.DataContext = ViewModel;

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
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

            // Build SavedSectionCut VM for display + plot
            var vm = new SavedSectionCutVM
            {
                SectionCutPrefix = data.SectionCutPrefix,
                StartNodeId = data.StartNodeId,
                XVector = data.XVector,
                YVector = data.YVector
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

            // Restore the input fields to match the selected saved set
            ViewModel.SectionCut.StartNodeId = data.StartNodeId;
            ViewModel.SectionCut.AreaIds = data.AreaIds ?? new List<string>();
            ViewModel.SectionCut.XVector = data.XVector;
            ViewModel.SectionCut.YVector = data.YVector;
            ViewModel.SectionCut.SectionCutPrefix = data.SectionCutPrefix;

            // Build SavedSectionCut VM for display + plot
            var vm = new SavedSectionCutVM
            {
                SectionCutPrefix = data.SectionCutPrefix,
                StartNodeId = data.StartNodeId,
                XVector = data.XVector,
                YVector = data.YVector
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
        }

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
