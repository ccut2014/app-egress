using System.Net.NetworkInformation;
using AppEgress.Config;
using AppEgress.Native;
using AppEgress.Localization;

namespace AppEgress.Core;

internal sealed record RuleView(Rule Rule, RuleState State, AdapterInfo? Target, RuleStats Stats);

/// <summary>
/// Связывает настройки, адаптеры и перехват: строит снимок правил, применяет его к PacketRouter,
/// сбрасывает соединения, которые нужно перенести, и следит за изменениями сети.
/// </summary>
internal sealed class RuleEngine : IDisposable
{
    private readonly ProcessResolver _resolver = new();
    private readonly PacketProcessor _processor;
    private readonly PacketRouter _router;
    private readonly ConnectionMonitor _monitor;
    private readonly Dictionary<Guid, RuleStats> _stats = new();
    private readonly object _rebuildLock = new();
    private readonly System.Threading.Timer _debounce;
    private RoutingSnapshot _snapshot = RoutingSnapshot.Empty;
    private IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();
    private IReadOnlyList<RuleView> _views = Array.Empty<RuleView>();
    private int _generation;
    private bool _resolverStarted;
    private bool _disposed;

    public AppSettings Settings { get; }

    public string Status { get; private set; } = Loc.T("StatusStarting");
    public bool HasError { get; private set; }

    public IReadOnlyList<AdapterInfo> Adapters => Volatile.Read(ref _adapters);
    public IReadOnlyList<RuleView> Views => Volatile.Read(ref _views);

    /// <summary>Состояние изменилось. Вызывается не из UI-потока.</summary>
    public event Action? Changed;

    public RuleEngine(AppSettings settings)
    {
        Settings = settings;
        _processor = new PacketProcessor(_resolver);
        _processor.Log += Logger.Write;
        _router = new PacketRouter(_processor);
        _router.Log += Logger.Write;
        _resolver.Log += Logger.Write;
        _monitor = new ConnectionMonitor(_resolver, () => Adapters);
        _debounce = new System.Threading.Timer(_ => Rebuild(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        RequestRebuild();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Windows присылает пачку событий подряд, поэтому пересобираем один раз после затишья.
        _debounce.Change(1500, Timeout.Infinite);
    }

    /// <summary>Сохраняет настройки и применяет правила.</summary>
    public void SaveAndApply()
    {
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (Exception ex)
        {
            Logger.Write(Loc.T("LogSettingsSaveFailed", ex.Message));
        }
        RequestRebuild();
    }

    public void RequestRebuild() => Task.Run(Rebuild);

    public void RefreshAdaptersNow() => Volatile.Write(ref _adapters, AdapterService.GetAll());

    private RuleStats StatsFor(Guid id)
    {
        lock (_stats)
        {
            if (!_stats.TryGetValue(id, out var s))
                _stats[id] = s = new RuleStats();
            return s;
        }
    }

    private void Rebuild()
    {
        lock (_rebuildLock)
        {
            if (_disposed)
                return;
            try
            {
                RebuildCore();
            }
            catch (Exception ex)
            {
                SetStatus(Loc.T("StatusError", ex.Message), error: true);
                Logger.Write(Loc.T("LogApplyError", ex));
            }
        }
        Changed?.Invoke();
    }

    private void RebuildCore()
    {
        var adapters = AdapterService.GetAll();
        Volatile.Write(ref _adapters, adapters);

        var views = new List<RuleView>();
        var active = new List<ActiveRule>();
        List<Rule> rules;
        lock (Settings)
            rules = Settings.Rules.ToList();
        foreach (var rule in rules)
        {
            var target = rule.Mode == RuleMode.Monitor ? null : adapters.FirstOrDefault(a => a.Id == rule.AdapterId);
            RuleState state;
            if (!rule.Enabled || Settings.Paused || string.IsNullOrWhiteSpace(rule.ProgramPath))
                state = RuleState.Disabled;
            else if (rule.Mode == RuleMode.Monitor)
                state = RuleState.Monitoring;
            else if (target is { IsUsable: true })
                state = RuleState.Active;
            else
                state = rule.KillSwitch ? RuleState.Blocking : RuleState.Waiting;

            var stats = StatsFor(rule.Id);
            views.Add(new RuleView(rule, state, target, stats));
            if (rule.Mode == RuleMode.Route && state != RuleState.Disabled)
                active.Add(new ActiveRule { Rule = rule, State = state, Target = target, Stats = stats });
        }
        Volatile.Write(ref _views, views);
        _monitor.Configure(views.Where(v => v.State == RuleState.Monitoring).Select(v => v.Rule));

        var byIndex = new Dictionary<uint, AdapterInfo>();
        foreach (var a in adapters)
            if (a.IfIndex != 0)
                byIndex.TryAdd(a.IfIndex, a);

        var previous = _snapshot;
        var snapshot = new RoutingSnapshot(active, byIndex, Settings.ExcludeLocalNetworks, ++_generation);

        if (snapshot.NeedsInterception && !_resolverStarted)
        {
            try
            {
                _resolver.Start();
                _resolverStarted = true;
            }
            catch (WinDivertException ex)
            {
                SetStatus(ex.Message, error: true);
                Logger.Write(ex.Message);
                return;
            }
        }

        List<FlowEntry> invalid;
        try
        {
            invalid = _router.Apply(snapshot);
        }
        catch (WinDivertException ex)
        {
            SetStatus(ex.Message, error: true);
            Logger.Write(ex.Message);
            return;
        }
        _snapshot = snapshot;
        if (!snapshot.NeedsInterception && _resolverStarted)
        {
            _resolver.Stop();
            _resolverStarted = false;
        }

        int reset = ConnectionResetter.ResetNatFlows(invalid);

        if (Settings.ResetExistingConnections)
        {
            foreach (var r in active)
            {
                if (!ChangedSincePrevious(r, previous))
                    continue;
                int n = ConnectionResetter.ResetForRule(r, snapshot, _resolver, adapters);
                if (n > 0)
                    Logger.Write(Loc.T("LogConnectionsReset", r.Rule.DisplayName, n));
                reset += n;
            }
        }

        foreach (var v in views)
        {
            if (v.State == RuleState.Disabled)
                continue;
            if (v.State == RuleState.Monitoring)
                continue; // Connection metadata is kept in memory, not written into the application log.
            string where = v.Target?.DisplayText ?? (string.IsNullOrEmpty(v.Rule.AdapterName) ? Loc.T("AdapterNotFound") : v.Rule.AdapterName);
            Logger.Write(Loc.T("LogRuleState", v.Rule.DisplayName, where, DescribeState(v.State)));
        }

        int activeCount = active.Count(r => r.State == RuleState.Active);
        int monitorCount = views.Count(v => v.State == RuleState.Monitoring);
        if (Settings.Paused)
            SetStatus(Loc.T("StatusPaused"), error: false);
        else if (!snapshot.NeedsInterception)
            SetStatus(monitorCount > 0 ? Loc.T("StatusMonitoring", monitorCount)
                : active.Count == 0 ? Loc.T("StatusNoRules") : Loc.T("StatusWaitingAdapters"), error: false);
        else
            SetStatus(Loc.T("StatusRunning", activeCount, views.Count), error: false);
    }

    /// <summary>Правило только что включилось или сменило целевой адрес: старые соединения нужно перенести.</summary>
    private static bool ChangedSincePrevious(ActiveRule r, RoutingSnapshot previous)
    {
        if (r.State is not (RuleState.Active or RuleState.Blocking))
            return false;
        var old = previous.Rules.FirstOrDefault(p => p.Rule.Id == r.Rule.Id);
        if (old == null || old.State != r.State)
            return true;
        if (!string.Equals(old.Rule.ProgramPath, r.Rule.ProgramPath, StringComparison.OrdinalIgnoreCase))
            return true;
        if (r.State == RuleState.Active)
            return old.Target?.IfIndex != r.Target?.IfIndex || old.Target?.IPv4 != r.Target?.IPv4;
        return false;
    }

    public static string DescribeState(RuleState s) => s switch
    {
        RuleState.Active => Loc.T("StateActiveLower"),
        RuleState.Blocking => Loc.T("StateBlockingLong"),
        RuleState.Waiting => Loc.T("StateWaitingLong"),
        RuleState.Monitoring => Loc.T("StateMonitoring"),
        _ => Loc.T("StateDisabledLower"),
    };

    private void SetStatus(string text, bool error)
    {
        Status = text;
        HasError = error;
    }

    /// <summary>PID запущенных процессов, которые подпадают под правило.</summary>
    public List<uint> FindProcesses(Rule rule) => _resolver.FindProcesses(rule.Matches);

    public MonitoringSnapshot GetMonitoringSnapshot(Guid id) => _monitor.GetSnapshot(id);

    public void PruneCaches() => _resolver.Prune();

    public void Dispose()
    {
        lock (_rebuildLock)
        {
            _disposed = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            _debounce.Dispose();
            _monitor.Dispose();
            _router.Dispose();
            _resolver.Dispose();
        }
    }
}
