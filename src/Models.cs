// View models bound by the XAML. Written for the in-box C# 5 compiler, so no
// string interpolation, null-conditional operators or expression-bodied members.
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AvdHealthCheck
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public enum CheckState { Pending, Running, Pass, Warn, Fail }

    /// <summary>One endpoint row in the Connectivity list.</summary>
    public class CheckItem : Observable
    {
        public EndpointDef Def { get; set; }
        public string Group { get; set; }
        public string Name { get; set; }
        public string Target { get; set; }
        public bool Required { get; set; }

        private CheckState _state;
        public CheckState State
        {
            get { return _state; }
            set { Set(ref _state, value); }
        }

        private string _detail;
        public string Detail
        {
            get { return _detail; }
            set { Set(ref _detail, value); }
        }

        private string _latency;
        public string Latency
        {
            get { return _latency; }
            set { Set(ref _latency, value); }
        }

        public void Reset()
        {
            State = CheckState.Pending;
            Detail = "Waiting";
            Latency = "";
        }
    }

    /// <summary>One label/value row in the This device card.</summary>
    public class DeviceField : Observable
    {
        public DeviceField(string label) { Label = label; _value = "…"; }

        public string Label { get; private set; }

        private string _value;
        public string Value
        {
            get { return _value; }
            set { Set(ref _value, value); }
        }
    }

    public class MainViewModel : Observable
    {
        public MainViewModel()
        {
            Checks = new ObservableCollection<CheckItem>();
            Device = new ObservableCollection<DeviceField>();
            CopyLabel = "Copy report";
        }

        public ObservableCollection<CheckItem> Checks { get; private set; }
        public ICollectionView ChecksView { get; set; }
        public ObservableCollection<DeviceField> Device { get; private set; }

        private bool _isRunning;
        public bool IsRunning { get { return _isRunning; } set { if (Set(ref _isRunning, value)) Raise("IsIdle"); } }
        public bool IsIdle { get { return !_isRunning; } }

        private double _progress;
        public double Progress { get { return _progress; } set { Set(ref _progress, value); } }

        // Header pill: Running | Pass | Warn | Fail
        private string _overallState = "Running";
        public string OverallState { get { return _overallState; } set { Set(ref _overallState, value); } }

        private string _overallText = "Starting checks";
        public string OverallText { get { return _overallText; } set { Set(ref _overallText, value); } }

        // Summary cards
        private string _connValue = "–";
        public string ConnValue { get { return _connValue; } set { Set(ref _connValue, value); } }
        private string _connSub = "Waiting";
        public string ConnSub { get { return _connSub; } set { Set(ref _connSub, value); } }

        private string _downValue = "–";
        public string DownValue { get { return _downValue; } set { Set(ref _downValue, value); } }
        private string _downSub = "Waiting";
        public string DownSub { get { return _downSub; } set { Set(ref _downSub, value); } }

        private string _upValue = "–";
        public string UpValue { get { return _upValue; } set { Set(ref _upValue, value); } }
        private string _upSub = "Waiting";
        public string UpSub { get { return _upSub; } set { Set(ref _upSub, value); } }

        private string _latValue = "–";
        public string LatValue { get { return _latValue; } set { Set(ref _latValue, value); } }
        private string _latSub = "Waiting";
        public string LatSub { get { return _latSub; } set { Set(ref _latSub, value); } }

        private string _footer = "";
        public string Footer { get { return _footer; } set { Set(ref _footer, value); } }

        private string _copyLabel;
        public string CopyLabel { get { return _copyLabel; } set { Set(ref _copyLabel, value); } }

        // "Show issues only" filter on the Connectivity list
        private bool _hasIssues;
        public bool HasIssues { get { return _hasIssues; } set { Set(ref _hasIssues, value); } }

        private string _issuesLabel = "Show issues only";
        public string IssuesLabel { get { return _issuesLabel; } set { Set(ref _issuesLabel, value); } }
    }
}
