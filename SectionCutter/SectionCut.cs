using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SectionCutter
{
    public class SectionCut : INotifyPropertyChanged
    {
        private string _startNodeId;
        private List<string> _areaIds;
        private double _xVector;
        private double _yVector;
        private string _sectionCutPrefix;
        private double _heightAbove;
        private double _heightBelow;
        private int _numberOfCuts;
        private string _units;

        public string StartNodeId
        {
            get => _startNodeId;
            set => SetProperty(ref _startNodeId, value);
        }

        public List<string> AreaIds
        {
            get => _areaIds;
            set => SetProperty(ref _areaIds, value);
        }

        public double XVector
        {
            get => _xVector;
            set => SetProperty(ref _xVector, value);
        }

        public double YVector
        {
            get => _yVector;
            set => SetProperty(ref _yVector, value);
        }

        public string SectionCutPrefix
        {
            get => _sectionCutPrefix;
            set => SetProperty(ref _sectionCutPrefix, value);
        }

        public double HeightAbove
        {
            get => _heightAbove;
            set => SetProperty(ref _heightAbove, value);
        }

        public double HeightBelow
        {
            get => _heightBelow;
            set => SetProperty(ref _heightBelow, value);
        }

        public int NumberOfCuts
        {
            get => _numberOfCuts;
            set => SetProperty(ref _numberOfCuts, value);
        }

        public string Units
        {
            get => _units;
            set => SetProperty(ref _units, value);
        }

        public SectionCut(string startNodeId, List<string> areaIds, double xVector, double yVector,
                          string sectionCutPrefix, double heightAbove, double heightBelow,
                          int numberOfCuts, string units)
        {
            StartNodeId = startNodeId;
            AreaIds = areaIds;
            XVector = xVector;
            YVector = yVector;
            SectionCutPrefix = sectionCutPrefix;
            HeightAbove = heightAbove;
            HeightBelow = heightBelow;
            NumberOfCuts = numberOfCuts;
            Units = units;
        }

        public SectionCut() { }

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
