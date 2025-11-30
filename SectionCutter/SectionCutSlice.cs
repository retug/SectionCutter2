using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SectionCutter
{
    /// <summary>
    /// Represents a single physical section cut (slice) generated from a SectionCut definition.
    /// Typically corresponds to one ETABS section cut line / quad group.
    /// </summary>
    public class SectionCutSlice : INotifyPropertyChanged
    {
        private int _index;
        private string _name;

        // Local coordinates in the section-cut coordinate system (U–V plane)
        private double _localStartU;
        private double _localStartV;
        private double _localEndU;
        private double _localEndV;

        // Optional: length of the cut in local coordinates
        private double _length;

        public int Index
        {
            get => _index;
            set => SetProperty(ref _index, value);
        }

        /// <summary>
        /// Name of the slice (e.g. "SC_0001").
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>
        /// Local U coordinate of the start point.
        /// </summary>
        public double LocalStartU
        {
            get => _localStartU;
            set => SetProperty(ref _localStartU, value);
        }

        /// <summary>
        /// Local V coordinate of the start point.
        /// </summary>
        public double LocalStartV
        {
            get => _localStartV;
            set => SetProperty(ref _localStartV, value);
        }

        /// <summary>
        /// Local U coordinate of the end point.
        /// </summary>
        public double LocalEndU
        {
            get => _localEndU;
            set => SetProperty(ref _localEndU, value);
        }

        /// <summary>
        /// Local V coordinate of the end point.
        /// </summary>
        public double LocalEndV
        {
            get => _localEndV;
            set => SetProperty(ref _localEndV, value);
        }

        /// <summary>
        /// Length of the slice in local coordinates.
        /// </summary>
        public double Length
        {
            get => _length;
            set => SetProperty(ref _length, value);
        }

        public SectionCutSlice()
        {
        }

        public SectionCutSlice(
            int index,
            string name,
            double localStartU,
            double localStartV,
            double localEndU,
            double localEndV,
            double length)
        {
            Index = index;
            Name = name;
            LocalStartU = localStartU;
            LocalStartV = localStartV;
            LocalEndU = localEndU;
            LocalEndV = localEndV;
            Length = length;
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
