using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SectionCutter.ViewModels
{
    public class SCViewModel : INotifyPropertyChanged
    {
        private SectionCut _sectionCut = new SectionCut();
        private bool _canCreate;

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

        public SCViewModel()
        {
            CreateCommand = new RelayCommand(Create, () => CanCreate);
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

