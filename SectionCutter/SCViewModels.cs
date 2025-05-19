using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ETABSv1;

namespace SectionCutter.ViewModels
{
    public class SCViewModel : INotifyPropertyChanged
    {
        private readonly cSapModel _sapModel;
        private SectionCut _sectionCut = new SectionCut();
        private bool _canCreate;
        private string _startNodeOutputText = "[Start Node Info Here]";
        
        public SectionCut SectionCut
        {
            get => _sectionCut;
            set => SetProperty(ref _sectionCut, value);
        }

        public bool CanCreate
        {
            get => _canCreate;
            private set => SetProperty(ref _canCreate, value);
        }

        public ICommand CreateCommand { get; }
        public ICommand GetStartNodeCommand { get; }

        public SCViewModel(cSapModel sapModel)
        {
            _sapModel = sapModel;
            SectionCut = new SectionCut();
            SectionCut.PropertyChanged += (_, __) => ValidateFields(); 
            CreateCommand = new RelayCommand(Create, () => CanCreate);
            GetStartNodeCommand = new RelayCommand(ExecuteGetStartNode, () => true); 
        }

        public void ValidateFields()
        {
            CanCreate =
                !string.IsNullOrWhiteSpace(SectionCut.SectionCutPrefix) &&
                SectionCut.XVector > 0 &&
                SectionCut.YVector > 0 &&
                SectionCut.HeightAbove > 0 &&
                SectionCut.HeightBelow > 0 &&
                SectionCut.NumberOfCuts > 0;

            (CreateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void Create()
        {
            // Logic to handle the creation
            System.Windows.MessageBox.Show($"SectionCut Created:\nPrefix = {SectionCut.SectionCutPrefix}\nX = {SectionCut.XVector}, Y = {SectionCut.YVector}");
        }

        private void ExecuteGetStartNode()
        {
            int numberItems = 0;
            int[] objectType = null;
            string[] objectName = null;
            _sapModel.SelectObj.GetSelected(ref numberItems, ref objectType, ref objectName);

            if (objectType == null)
            {
                MessageBox.Show("Select node first, then click the button");
                return;
            }

            if (objectType.Length > 1 || objectType[0] != 1)
            {
                MessageBox.Show("Select only one node");
                return;
            }

            SectionCut.StartNodeId = objectName[0];
            StartNodeOutputText = $"Unique ID selected = \"{objectName[0]}\"";
        }

        public string StartNodeOutputText
        {
            get => _startNodeOutputText;
            set => SetProperty(ref _startNodeOutputText, value);
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
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => _execute();

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

