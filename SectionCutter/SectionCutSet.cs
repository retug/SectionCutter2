using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SectionCutter
{
    /// <summary>
    /// Represents a set of section cuts generated from a single SectionCut definition.
    /// This is the "result" object that groups:
    /// - The original definition (inputs)
    /// - The individual generated slices
    /// </summary>
    public class SectionCutSet : INotifyPropertyChanged
    {
        private SectionCut _definition;
        private ObservableCollection<SectionCutSlice> _slices;

        // In the future, you can extend this class to hold ETABS table records,
        // force results, etc., but for now we keep it lean.

        /// <summary>
        /// The input definition used to generate this set of section cuts.
        /// </summary>
        public SectionCut Definition
        {
            get => _definition;
            set => SetProperty(ref _definition, value);
        }

        /// <summary>
        /// The collection of generated section cut slices.
        /// ObservableCollection is used for easy data binding in MVVM.
        /// </summary>
        public ObservableCollection<SectionCutSlice> Slices
        {
            get => _slices;
            set => SetProperty(ref _slices, value);
        }

        public SectionCutSet()
        {
            _slices = new ObservableCollection<SectionCutSlice>();
        }

        public SectionCutSet(SectionCut definition, IEnumerable<SectionCutSlice> slices)
        {
            Definition = definition;
            _slices = new ObservableCollection<SectionCutSlice>(
                slices ?? Array.Empty<SectionCutSlice>());
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
}
