using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ETABSv1;
using SectionCutter.ViewModels;

namespace SectionCutter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    /// 
    

    public partial class MainWindow : Window
    {
        private cPluginCallback _Plugin;
        private cSapModel _SapModel;
        public SCViewModel ViewModel { get; set; }

        public MainWindow(cSapModel SapModel, cPluginCallback Plugin)
        {

            InitializeComponent();

            
            ViewModel = new SCViewModel();
            this.DataContext = ViewModel;
            _SapModel = SapModel;
            _Plugin = Plugin;

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
            //test tssed
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {

        }



        private void Button_Click(object sender, RoutedEventArgs e)
        {

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

        private void GetStartNode_Click(object sender, RoutedEventArgs e)
        {
            StartNodeOutput.Text = "Start node selected [mock]";
        }

        private void GetAreas_Click(object sender, RoutedEventArgs e)
        {
            AreasOutput.Text = "Areas retrieved [mock]";
        }

        private void DecimalInput_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // Allow digits and one decimal
            e.Handled = !decimal.TryParse(((TextBox)sender).Text + e.Text, out decimal result) || result < 0;
        }

        private void IntegerInput_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
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
        }

        private void KnMCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (KipFtCheckBox != null)
                KipFtCheckBox.IsChecked = false;
        }

        private void ValidateCreateButton()
        {
            bool isValid = double.TryParse(XVectorInput.Text, out double x) &&
                           double.TryParse(YVectorInput.Text, out double y) &&
                           double.TryParse(HeightAboveInput.Text, out double ha) &&
                           double.TryParse(HeightBelowInput.Text, out double hb) &&
                           int.TryParse(SectionCutCountInput.Text, out int count) &&
                           !string.IsNullOrWhiteSpace(SectionCutTitleInput.Text);

            CreateSectionsButton.IsEnabled = isValid;
            CreateSectionsButton.Background = isValid
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff8c69"))
                : Brushes.Gray;
        }

        private void CreateSectionsButton_Click(object sender, RoutedEventArgs e)
        {
            var sectionCut = new SectionCut
            {
                StartNodeId = StartNodeOutput.Text,
                AreaIds = new List<string> { AreasOutput.Text },
                XVector = double.Parse(XVectorInput.Text),
                YVector = double.Parse(YVectorInput.Text),
                SectionCutPrefix = SectionCutTitleInput.Text,
                HeightAbove = double.Parse(HeightAboveInput.Text),
                HeightBelow = double.Parse(HeightBelowInput.Text),
                NumberOfCuts = int.Parse(SectionCutCountInput.Text),
                Units = KipFtCheckBox.IsChecked == true ? "kip, ft" : "kN, M"
            };

            MessageBox.Show($"SectionCut Created:\nPrefix = {sectionCut.SectionCutPrefix}\nX = {sectionCut.XVector}, Y = {sectionCut.YVector}");
        }


    }
}