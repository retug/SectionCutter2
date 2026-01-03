using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ETABSv1;
using SectionCutter.ViewModels;

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

            this.DataContext = ViewModel;

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // On load: try to read SectionCut.json and restore the last-used inputs
            try
            {
                if (_jsonStore.TryLoad(out var data) && data != null)
                {
                    // Populate the ViewModel SectionCut inputs
                    ViewModel.SectionCut.StartNodeId = data.StartNodeId;
                    ViewModel.SectionCut.AreaIds = data.AreaIds ?? new System.Collections.Generic.List<string>();
                    ViewModel.SectionCut.XVector = data.XVector;
                    ViewModel.SectionCut.YVector = data.YVector;
                    ViewModel.SectionCut.SectionCutPrefix = data.SectionCutPrefix;

                    // Friendly text outputs so the user knows it loaded
                    if (!string.IsNullOrWhiteSpace(data.StartNodeId))
                        ViewModel.StartNodeOutputText = $"Start Node loaded: \"{data.StartNodeId}\"";

                    int aCount = data.AreaIds?.Count ?? 0;
                    int oCount = data.OpeningIds?.Count ?? 0;
                    ViewModel.AreasOutputText = $"Loaded {aCount} area object(s) ({oCount} opening(s) detected).";

                    // Check if section cuts already exist in the model for this prefix
                    if (!string.IsNullOrWhiteSpace(data.SectionCutPrefix))
                    {
                        var existing = _jsonStore.GetExistingSectionCutNamesByPrefix(data.SectionCutPrefix);
                        if (existing.Count > 0)
                        {
                            MessageBox.Show(
                                $"Found {existing.Count} existing section cut(s) in the ETABS model with prefix \"{data.SectionCutPrefix}\".\n\n" +
                                $"Example: {existing[0]}",
                                "Existing Section Cuts Detected",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                        }
                    }

                    // Recompute Create button state
                    ViewModel.ValidateFields();
                }
            }
            catch (Exception ex)
            {
                // Do not crash plugin on load; just notify
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
