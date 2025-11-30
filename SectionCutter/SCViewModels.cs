using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
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
        /// Called from the MainWindow's GetAreas_Click handler.
        /// Uses the current ETABS selection to collect area objects.
        /// </summary>
        public void GetAreasFromSelection()
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

            for (int i = 0; i < numberItems; i++)
            {
                // ETABS object type 5 = area object
                if (objectType[i] == 5)
                {
                    areaIds.Add(objectName[i]);
                }
            }

            if (areaIds.Count == 0)
            {
                MessageBox.Show("No area objects were found in the current selection.");
                return;
            }

            SectionCut.AreaIds = areaIds;
            AreasOutputText = $"Selected {areaIds.Count} area(s): {string.Join(", ", areaIds)}";

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
