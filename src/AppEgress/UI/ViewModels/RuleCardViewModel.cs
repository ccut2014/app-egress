using System.Collections.ObjectModel;
using System.Windows.Media;
using AppEgress.Core;
using AppEgress.Localization;

namespace AppEgress.UI.ViewModels;

/// <summary>Карточка правила: состояние, адаптер, счётчики и график скорости.</summary>
internal sealed class RuleCardViewModel : ObservableObject
{
    private readonly Action<RuleCardViewModel> _onToggled;
    private readonly SpeedHistory _down = new();
    private readonly SpeedHistory _up = new();
    private long _lastIn = -1, _lastOut = -1;

    private RuleView _view;
    private string _stateText = "", _stateKind = "Off", _adapterText = "", _adapterGlyph = Glyphs.Globe;
    private string _statsText = "", _downSpeed = Format.Speed(0), _upSpeed = Format.Speed(0);
    private int _processes;
    private double[] _downHistory = new double[SpeedHistory.Length];
    private double[] _upHistory = new double[SpeedHistory.Length];
    private MonitoringSnapshot _monitoring = MonitoringSnapshot.Empty;
    private string _monitorSummary = "", _proxySummary = "";
    private string _exitSummary = "", _proxyEvidence = "", _pathNotes = "";

    public ObservableCollection<MonitoringConnectionRow> Connections { get; } = new();
    public bool IsMonitorOnly => Rule.Mode == RuleMode.Monitor;
    public string MonitorSummary { get => _monitorSummary; private set => Set(ref _monitorSummary, value); }
    public string ProxySummary { get => _proxySummary; private set => Set(ref _proxySummary, value); }
    public string ExitSummary { get => _exitSummary; private set => Set(ref _exitSummary, value); }
    public string ProxyEvidence { get => _proxyEvidence; private set => Set(ref _proxyEvidence, value); }
    public string PathNotes { get => _pathNotes; private set => Set(ref _pathNotes, value); }

    public RuleCardViewModel(RuleView view, Action<RuleCardViewModel> onToggled)
    {
        _view = view;
        _onToggled = onToggled;
        Icon = IconCache.Get(view.Rule.ProgramPath);
        Update(view, paused: false);
    }

    public Guid Id => _view.Rule.Id;
    public Rule Rule => _view.Rule;
    public ImageSource? Icon { get; private set; }
    public bool HasIcon => Icon != null;
    public string Name => _view.Rule.DisplayName;
    public string PathText => _view.Rule.MatchesByNameOnly ? Loc.T("AnyFolder", _view.Rule.ProgramPath) : _view.Rule.ProgramPath;

    public bool IsEnabled
    {
        get => _view.Rule.Enabled;
        set
        {
            if (_view.Rule.Enabled == value)
                return;
            _view.Rule.Enabled = value;
            OnPropertyChanged();
            _onToggled(this);
        }
    }

    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }

    /// <summary>Ok, Warn, Error или Off: от этого зависит цвет плашки состояния.</summary>
    public string StateKind { get => _stateKind; private set => Set(ref _stateKind, value); }

    public string AdapterText { get => _adapterText; private set => Set(ref _adapterText, value); }
    public string AdapterGlyph { get => _adapterGlyph; private set => Set(ref _adapterGlyph, value); }
    public string StatsText { get => _statsText; private set => Set(ref _statsText, value); }
    public string DownSpeed { get => _downSpeed; private set => Set(ref _downSpeed, value); }
    public string UpSpeed { get => _upSpeed; private set => Set(ref _upSpeed, value); }
    public double[] DownHistory { get => _downHistory; private set => Set(ref _downHistory, value); }
    public double[] UpHistory { get => _upHistory; private set => Set(ref _upHistory, value); }

    public int Processes
    {
        get => _processes;
        set
        {
            if (Set(ref _processes, value))
                UpdateStats();
        }
    }

    public void Update(RuleView view, bool paused)
    {
        bool pathChanged = !string.Equals(view.Rule.ProgramPath, _view.Rule.ProgramPath, StringComparison.OrdinalIgnoreCase);
        bool modeChanged = view.Rule.Mode != _view.Rule.Mode;
        _view = view;
        if (pathChanged || modeChanged)
        {
            _monitoring = MonitoringSnapshot.Empty;
            Connections.Clear();
        }
        if (pathChanged)
        {
            Icon = IconCache.Get(view.Rule.ProgramPath);
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(HasIcon));
        }
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsMonitorOnly));

        var target = view.Target;
        if (IsMonitorOnly)
        {
            AdapterText = Loc.T("MonitorNoAdapter");
            AdapterGlyph = Glyphs.Globe;
        }
        else if (target != null)
        {
            string ip = target.IPv4 is { } a ? Addr.Format(a) : Loc.T("NoIPv4");
            AdapterText = $"{target.Name} · {ip}";
            AdapterGlyph = Glyphs.ForAdapter(target.Kind);
        }
        else
        {
            AdapterText = Loc.T("AdapterMissingCard", string.IsNullOrEmpty(view.Rule.AdapterName) ? Loc.T("AdapterWord") : view.Rule.AdapterName);
            AdapterGlyph = Glyphs.Globe;
        }

        (StateText, StateKind) = view.State switch
        {
            RuleState.Active => (Loc.T("StateActive"), "Ok"),
            RuleState.Blocking => (Loc.T("StateBlocking"), "Error"),
            RuleState.Waiting => (Loc.T("StateWaiting"), "Warn"),
            RuleState.Monitoring => (Loc.T("StateMonitoring"), "Ok"),
            _ => (paused && view.Rule.Enabled ? Loc.T("StatePaused") : Loc.T("StateOff"), "Off"),
        };
        if (IsMonitorOnly)
            UpdateMonitoring(_monitoring);
        UpdateStats();
    }

    public void UpdateMonitoring(MonitoringSnapshot snapshot)
    {
        _monitoring = _view.State == RuleState.Monitoring ? snapshot : MonitoringSnapshot.Empty;
        var s = _monitoring;
        var presentation = MonitoringPresentation.Create(s);
        var rows = presentation.Rows;
        for (int i = 0; i < rows.Count; i++)
            if (i >= Connections.Count) Connections.Add(rows[i]);
            else if (Connections[i] != rows[i]) Connections[i] = rows[i];
        while (Connections.Count > rows.Count) Connections.RemoveAt(Connections.Count - 1);

        MonitorSummary = _view.State != RuleState.Monitoring ? Loc.T("MonitorStopped")
            : s.CapturedAt is not { } captured ? Loc.T("MonitorPending")
            : Loc.T("MonitorSnapshot", captured.ToLocalTime().ToString("HH:mm:ss"), s.Connections.Count, s.TcpCount + s.UdpCount);
        if (s.ReadErrors.Count > 0)
        {
            MonitorSummary += " · " + Loc.T("MonitorReadError", string.Join(", ", s.ReadErrors));
            StateText = Loc.T("MonitorPartial");
            StateKind = "Warn";
        }
        else if (_view.State == RuleState.Monitoring)
        {
            StateText = Loc.T("StateMonitoring");
            StateKind = "Ok";
        }
        ProxySummary = s.CapturedAt == null ? "" : !s.ProxyReadable ? Loc.T("MonitorProxyReadError")
            : Loc.T(s.ProxyEnabled ? "MonitorProxyOn" : "MonitorProxyOff");
        if (s.PacConfigured) ProxySummary += " · " + Loc.T("MonitorPac");
        ExitSummary = s.CapturedAt == null ? MonitorSummary : presentation.ExitSummary;
        ProxyEvidence = s.CapturedAt == null ? "" : presentation.ProxyEvidence;
        PathNotes = s.CapturedAt == null ? "" : presentation.Notes;
        if (IsMonitorOnly)
            AdapterText = s.CapturedAt == null ? Loc.T("MonitorNoAdapter") : presentation.CardText;
        UpdateStats();
    }

    /// <summary>Раз в секунду: скорость и график.</summary>
    public void Sample()
    {
        if (IsMonitorOnly)
        {
            CurrentDown = CurrentUp = 0;
            _lastIn = _lastOut = -1;
            UpdateStats();
            return;
        }
        long inBytes = Interlocked.Read(ref _view.Stats.BytesIn);
        long outBytes = Interlocked.Read(ref _view.Stats.BytesOut);
        double down = _lastIn < 0 ? 0 : Math.Max(0, inBytes - _lastIn);
        double up = _lastOut < 0 ? 0 : Math.Max(0, outBytes - _lastOut);
        _lastIn = inBytes;
        _lastOut = outBytes;
        _down.Push(down);
        _up.Push(up);
        DownHistory = _down.ToArray();
        UpHistory = _up.ToArray();
        DownSpeed = Format.Speed(down);
        UpSpeed = Format.Speed(up);
        CurrentDown = down;
        CurrentUp = up;
        UpdateStats();
    }

    public double CurrentDown { get; private set; }
    public double CurrentUp { get; private set; }

    private void UpdateStats()
    {
        if (IsMonitorOnly)
        {
            StatsText = Loc.Plural("PluralProcesses", _processes) + "  ·  "
                + (_monitoring.CapturedAt == null ? Loc.T("MonitorMetadataOnly")
                    : Loc.T("MonitorCounts", _monitoring.TcpCount, _monitoring.UdpCount));
            return;
        }
        var s = _view.Stats;
        var parts = new List<string>
        {
            Loc.Plural("PluralProcesses", _processes),
            Loc.Plural("PluralConnections", Volatile.Read(ref s.ActiveFlows)),
            "↑ " + Format.Bytes(Interlocked.Read(ref s.BytesOut)),
            "↓ " + Format.Bytes(Interlocked.Read(ref s.BytesIn)),
        };
        long reset = Interlocked.Read(ref s.ConnectionsReset);
        if (reset > 0)
            parts.Add(Loc.T("StatsReset", reset));
        long blocked = Interlocked.Read(ref s.PacketsBlocked);
        if (blocked > 0)
            parts.Add(Loc.T("StatsBlocked", blocked));
        long routeFailures = Interlocked.Read(ref s.RouteFailures);
        if (routeFailures > 0)
            parts.Add(Loc.T("StatsRouteFailures", routeFailures));
        StatsText = string.Join("  ·  ", parts);
        if (_view.State == RuleState.Active)
        {
            long lastFailure = Volatile.Read(ref s.LastRouteFailure);
            bool recentFailure = lastFailure > 0 && Environment.TickCount64 - lastFailure < 10000;
            (StateText, StateKind) = recentFailure
                ? (Loc.T("StateRouteIssue"), "Warn")
                : (Loc.T("StateActive"), "Ok");
        }
    }
}
