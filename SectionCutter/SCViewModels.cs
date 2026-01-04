using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ETABSv1;

namespace SectionCutter.ViewModels
{
    public class SCViewModel : INotifyPropertyChanged
    {
        private readonly cSapModel _sapModel;
        private readonly ISectionCutService _sectionCutService;

        private SectionCut _sectionCut = new SectionCut();
        private SectionCutSet _currentSet;
        private bool _canCreate;
        private string _startNodeOutputText = "[Start Node Info Here]";
        private string _areasOutputText = "[Area Info Here]";


        public event Action SectionCutsCreated;

        private SavedSectionCutVM _savedSectionCut;
        public SavedSectionCutVM SavedSectionCut
        {
            get => _savedSectionCut;
            set
            {
                if (SetProperty(ref _savedSectionCut, value))
                {
                    HasSavedSectionCutData = _savedSectionCut != null;
                }
            }
        }

        public ObservableCollection<string> SectionCutPrefixes { get; } = new();

        private string _selectedPrefix;
        public string SelectedPrefix
        {
            get => _selectedPrefix;
            set => SetProperty(ref _selectedPrefix, value);
        }

        private bool _hasSavedSectionCutData;
        public bool HasSavedSectionCutData
        {
            get => _hasSavedSectionCutData;
            private set => SetProperty(ref _hasSavedSectionCutData, value);
        }

        public SectionCut SectionCut
        {
            get => _sectionCut;
            set => SetProperty(ref _sectionCut, value);
        }

        /// <summary>
        /// The generated section cut set after Create is run.
        /// </summary>
        public SectionCutSet CurrentSet
        {
            get => _currentSet;
            private set => SetProperty(ref _currentSet, value);
        }

        public bool CanCreate
        {
            get => _canCreate;
            private set => SetProperty(ref _canCreate, value);
        }

        public string StartNodeOutputText
        {
            get => _startNodeOutputText;
            set => SetProperty(ref _startNodeOutputText, value);
        }

        public string AreasOutputText
        {
            get => _areasOutputText;
            set => SetProperty(ref _areasOutputText, value);
        }

        public ICommand CreateCommand { get; }
        public ICommand GetStartNodeCommand { get; }
        public ICommand GetAreasCommand { get; }

        // ---------------------------
        // Review Results bindings
        // ---------------------------

        // Prefix list for Review Results (separate from Create/Preview SelectedPrefix)
        public ObservableCollection<string> ResultsPrefixes { get; } = new();

        private string _selectedResultsPrefix;
        public string SelectedResultsPrefix
        {
            get => _selectedResultsPrefix;
            set => SetProperty(ref _selectedResultsPrefix, value);
        }

        // Load cases dropdown (with HasResults styling in XAML)
        public ObservableCollection<LoadCaseItem> LoadCases { get; } = new();

        private LoadCaseItem _selectedLoadCase;
        public LoadCaseItem SelectedLoadCase
        {
            get => _selectedLoadCase;
            set => SetProperty(ref _selectedLoadCase, value);
        }

        // DataGrid rows for results
        public ObservableCollection<SectionCutResultRow> ResultsRows { get; } = new();

        // Geometry for Results plots (bound just like SavedSectionCut plotting)
        public ObservableCollection<PointCollection> ResultsAreaPolygons { get; } = new();
        public ObservableCollection<PointCollection> ResultsOpeningPolygons { get; } = new();

        // Cuts for each plot (Plot1 and Plot2 use different "Value")
        public ObservableCollection<ResultCutPlotItem> ResultsPlot1Cuts { get; } = new();
        public ObservableCollection<ResultCutPlotItem> ResultsPlot2Cuts { get; } = new();


        // Optional plot dropdown selections (nice to keep in VM)
        private string _plot1Component = "Shear";
        public string Plot1Component
        {
            get => _plot1Component;
            set => SetProperty(ref _plot1Component, value);
        }

        private string _plot2Component = "Moment";
        public string Plot2Component
        {
            get => _plot2Component;
            set => SetProperty(ref _plot2Component, value);
        }

        public SCViewModel(cSapModel sapModel, ISectionCutService sectionCutService)
        {
            _sapModel = sapModel ?? throw new ArgumentNullException(nameof(sapModel));
            _sectionCutService = sectionCutService ?? throw new ArgumentNullException(nameof(sectionCutService));

            SectionCut = new SectionCut
            {
                AreaIds = new List<string>(),
                Units = "kip, ft"
            };

            // Re-validate whenever any SectionCut property changes
            SectionCut.PropertyChanged += (_, __) => ValidateFields();

            CreateCommand = new RelayCommand(Create, () => CanCreate);
            GetStartNodeCommand = new RelayCommand(ExecuteGetStartNode, () => true);
            GetAreasCommand = new RelayCommand(ExecuteGetAreas, () => true);

            ValidateFields();
        }

        /// <summary>
        /// Validates whether all required inputs are present so Create can run.
        /// </summary>
        public void ValidateFields()
        {
            bool hasDirection = Math.Abs(SectionCut.XVector) > 1e-8 || Math.Abs(SectionCut.YVector) > 1e-8;
            bool hasHeights = SectionCut.HeightAbove > 0.0 && SectionCut.HeightBelow >= 0.0;
            bool hasCuts = SectionCut.NumberOfCuts > 0;
            bool hasStartNode = !string.IsNullOrWhiteSpace(SectionCut.StartNodeId);
            bool hasAreas = SectionCut.AreaIds != null && SectionCut.AreaIds.Count > 0;
            bool hasPrefix = !string.IsNullOrWhiteSpace(SectionCut.SectionCutPrefix);

            CanCreate = hasDirection && hasHeights && hasCuts && hasStartNode && hasAreas && hasPrefix;

            (CreateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Called by CreateCommand when the user clicks "Create Sections".
        /// This delegates to the EtabsSectionCutService and stores the resulting SectionCutSet.
        /// </summary>
        private void Create()
        {
            try
            {
                CurrentSet = _sectionCutService.CreateEtabsSectionCuts(SectionCut);

                MessageBox.Show(
                    $"Created {CurrentSet?.Slices?.Count ?? 0} section cuts in ETABS.",
                    "Section Cuts Created",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                // ✅ Tell MainWindow to reload JSON + refresh dropdown + plot
                SectionCutsCreated?.Invoke();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error creating section cuts:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Uses the current ETABS selection to pick a single point object
        /// and stores its ID in SectionCut.StartNodeId.
        /// </summary>
        private void ExecuteGetStartNode()
        {
            int numberItems = 0;
            int[] objectType = null;
            string[] objectName = null;

            _sapModel.SelectObj.GetSelected(ref numberItems, ref objectType, ref objectName);

            if (objectType == null || objectName == null || numberItems == 0)
            {
                MessageBox.Show("Select a single joint (point) first, then click the button.");
                return;
            }

            if (numberItems != 1 || objectType[0] != 1) // 1 = point object
            {
                MessageBox.Show("Select exactly one joint (point) in ETABS.");
                return;
            }

            SectionCut.StartNodeId = objectName[0];
            StartNodeOutputText = $"Start Node selected: \"{objectName[0]}\"";

            ValidateFields();
        }

        /// <summary>
        /// Uses the current ETABS selection to collect area objects (floors/openings).
        /// Mirrors the behavior of your original getSelAreas_Click but lives in the VM.
        /// </summary>
        private void ExecuteGetAreas()
        {
            int numberItems = 0;
            int[] objectType = null;
            string[] objectName = null;

            _sapModel.SelectObj.GetSelected(ref numberItems, ref objectType, ref objectName);

            if (objectType == null || objectName == null || numberItems == 0)
            {
                MessageBox.Show("Select at least one area object, then click Get Areas.");
                return;
            }

            var areaIds = new List<string>();
            int openings = 0;
            int areasSelected = 0;

            for (int i = 0; i < numberItems; i++)
            {
                // ETABS object type 5 = area object (slab, wall, opening)
                if (objectType[i] == 5)
                {
                    string areaName = objectName[i];

                    bool isOpening = false;
                    _sapModel.AreaObj.GetOpening(areaName, ref isOpening);

                    if (isOpening)
                    {
                        openings += 1;
                    }
                    else
                    {
                        areasSelected += 1;
                    }

                    // Store all area IDs (including openings) for now.
                    // You can later filter if needed in the service.
                    areaIds.Add(areaName);
                }
            }

            if (areaIds.Count == 0)
            {
                MessageBox.Show("No area objects were found in the current selection.");
                return;
            }

            SectionCut.AreaIds = areaIds;

            AreasOutputText =
                $"Selected {areasSelected} area(s), {openings} opening(s).";

            ValidateFields();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

    }


    public class SavedSectionCutVM
    {
        public string SectionCutPrefix { get; set; }
        public string StartNodeId { get; set; }

        public double XVector { get; set; }
        public double YVector { get; set; }
        public string VectorLabel => $"X={XVector:0.###}, Y={YVector:0.###}";

        public ObservableCollection<string> AreaIds { get; } = new();
        public ObservableCollection<string> OpeningIds { get; } = new();

        // Plot data (local U/V space)
        public ObservableCollection<PointCollection> AreaPolygons { get; } = new();
        public ObservableCollection<PointCollection> OpeningPolygons { get; } = new();
        public ObservableCollection<SectionCutter.SectionCutPreviewControl.Segment> Cuts { get; } = new();

    }
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => _execute();

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
