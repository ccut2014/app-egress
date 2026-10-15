using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows.Input;
using System.Windows.Threading;
using TrafikRuler.Config;
using TrafikRuler.Core;
using TrafikRuler.Localization;

namespace TrafikRuler.UI.ViewModels;

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 300;

    private readonly RuleEngine _engine;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Queue<string> _log = new();
    private int _tick;
    private int _samplingAdapters;
    private bool _autostart;

    private string _statusText = Loc.T("StatusStarting"), _statusKind = "Off", _errorText = "", _totalSpeed = "", _logText = "";
    private bool _hasError, _isLogOpen;

    /// <summary>Редактор правила: (правило, новое ли) → изменённое правило или null. Задаёт окно.</summary>
    public Func<Rule, bool, Rule?>? EditRule { get; set; }

    /// <summary>Подтверждение: (заголовок, текст, кнопка) → да/нет. Задаёт окно.</summary>
    public Func<string, string, string, bool>? Confirm { get; set; }

    public ObservableCollection<RuleCardViewModel> Rules { get; } = new();
    public ObservableCollection<AdapterChipViewModel> Adapters { get; } = new();

    public ICommand AddRuleCommand { get; }
    public ICommand EditRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ToggleLogCommand { get; }

    public MainViewModel(RuleEngine engine)
    {
        _engine = engine;
        _dispatcher = Dispatcher.CurrentDispatcher;

        AddRuleCommand = new RelayCommand(AddRule);
        EditRuleCommand = new RelayCommand(p => EditRuleCard(p as RuleCardViewModel));
        DeleteRuleCommand = new RelayCommand(p => DeleteRule(p as RuleCardViewModel));
        TogglePauseCommand = new RelayCommand(TogglePause);
        RefreshCommand = new RelayCommand(() => _engine.RequestRebuild());
        OpenFolderCommand = new RelayCommand(OpenDataFolder);
        ToggleLogCommand = new RelayCommand(() => IsLogOpen = !IsLogOpen);
        LanguageOptions = new[] { new LanguageOptionViewModel(this, Loc.Auto, null) }
            .Concat(Loc.Languages.Select(l => new LanguageOptionViewModel(this, l.Code, l.NativeName)))
            .ToList();

        foreach (var line in Logger.Snapshot())
            AppendLog(line);
        Logger.Written += OnLogWritten;
        _engine.Changed += OnEngineChanged;
        Loc.Changed += OnLanguageChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        Task.Run(() =>
        {
            bool enabled = Autostart.IsEnabled();
            _dispatcher.BeginInvoke(() =>
            {
                _autostart = enabled;
                OnPropertyChanged(nameof(AutostartEnabled));
            });
        });

        SyncFromEngine();
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Ok, Warn, Error или Off.</summary>
    public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public bool IsPaused => _engine.Settings.Paused;
    public bool HasRules => Rules.Count > 0;
    public string TotalSpeed { get => _totalSpeed; private set => Set(ref _totalSpeed, value); }
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public bool IsLogOpen { get => _isLogOpen; set => Set(ref _isLogOpen, value); }

    public bool ExcludeLocalNetworks
    {
        get => _engine.Settings.ExcludeLocalNetworks;
        set
        {
            if (_engine.Settings.ExcludeLocalNetworks == value) return;
            _engine.Settings.ExcludeLocalNetworks = value;
            OnPropertyChanged();
            _engine.SaveAndApply();
        }
    }

    public bool ResetExistingConnections
    {
        get => _engine.Settings.ResetExistingConnections;
        set
        {
            if (_engine.Settings.ResetExistingConnections == value) return;
            _engine.Settings.ResetExistingConnections = value;
            OnPropertyChanged();
            _engine.SaveAndApply();
        }
    }

    public bool AutostartEnabled
    {
        get => _autostart;
        set
        {
            if (_autostart == value) return;
            _autostart = value;
            OnPropertyChanged();
            Task.Run(() =>
            {
                bool ok = value ? Autostart.Enable(out string error) : Autostart.Disable(out error);
                if (!ok)
                {
                    Logger.Write(Loc.T("LogAutostartFailed", error));
                    _dispatcher.BeginInvoke(() =>
                    {
                        _autostart = !value;
                        OnPropertyChanged(nameof(AutostartEnabled));
                    });
                }
            });
        }
    }

    /// <summary>Язык интерфейса: "auto", "en" или "ru". Переключается сразу, без перезапуска.</summary>
    public string LanguagePreference
    {
        get => _engine.Settings.Language;
        set
        {
            if (string.IsNullOrEmpty(value) || _engine.Settings.Language == value) return;
            _engine.Settings.Language = value;
            OnPropertyChanged();
            Loc.SetLanguage(value);
            Logger.Write(Loc.T("LogLanguageChanged", Loc.Language));
            // Пересборка правил заодно пересчитывает статус движка уже на новом языке.
            _engine.SaveAndApply();
        }
    }

    /// <summary>Подпись варианта «Авто» с языком, который он выберет: «Auto (English)».</summary>
    public string AutoLanguageLabel =>
        Loc.T("LanguageAuto", Loc.Languages.First(l => l.Code == Loc.DetectSystem()).NativeName);

    /// <summary>Варианты переключателя языка: «Авто» и все языки из <see cref="Loc.Languages"/>.</summary>
    public IReadOnlyList<LanguageOptionViewModel> LanguageOptions { get; }

    private void OnLanguageChanged() => _dispatcher.BeginInvoke(() =>
    {
        SyncFromEngine();
        OnPropertyChanged(nameof(AutoLanguageLabel));
        foreach (var o in LanguageOptions)
            o.Refresh();
    });
    private void OnEngineChanged() => _dispatcher.BeginInvoke(SyncFromEngine);

    private void OnLogWritten(string line) => _dispatcher.BeginInvoke(() => AppendLog(line));

    private void AppendLog(string line)
    {
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines)
            _log.Dequeue();
        LogText = string.Join(Environment.NewLine, _log);
    }

    /// <summary>Переносит состояние движка в карточки, сохраняя у них историю скорости.</summary>
    internal void SyncFromEngine() => Sync(_engine.Views, _engine.Adapters, _engine.Status, _engine.HasError);

    internal void Sync(IReadOnlyList<RuleView> views, IReadOnlyList<AdapterInfo> adapters, string status, bool hasError)
    {
        bool paused = _engine.Settings.Paused;
        var byId = Rules.ToDictionary(r => r.Id);
        for (int i = 0; i < views.Count; i++)
        {
            var v = views[i];
            if (byId.Remove(v.Rule.Id, out var card))
            {
                card.Update(v, paused);
                int idx = Rules.IndexOf(card);
                if (idx != i)
                    Rules.Move(idx, i);
            }
            else
            {
                Rules.Insert(i, new RuleCardViewModel(v, OnRuleToggled));
            }
        }
        foreach (var gone in byId.Values)
            Rules.Remove(gone);

        // Адаптеры: показываем подключённые с IPv4, а также те, на которые ссылаются правила.
        var used = views.Where(v => v.State == RuleState.Active && v.Target != null).Select(v => v.Target!.Id).ToHashSet();
        var visible = adapters.Where(a => a.IsUsable || used.Contains(a.Id)).ToList();
        var chips = Adapters.ToDictionary(a => a.Id);
        for (int i = 0; i < visible.Count; i++)
        {
            var a = visible[i];
            if (chips.Remove(a.Id, out var chip))
            {
                chip.Update(a);
                int idx = Adapters.IndexOf(chip);
                if (idx != i)
                    Adapters.Move(idx, i);
            }
            else
            {
                chip = new AdapterChipViewModel(a);
                Adapters.Insert(i, chip);
            }
            chip.IsUsed = used.Contains(a.Id);
        }
        foreach (var gone in chips.Values)
            Adapters.Remove(gone);

        HasError = hasError;
        ErrorText = hasError ? status : "";
        int active = views.Count(v => v.State == RuleState.Active);
        if (hasError)
        {
            StatusKind = "Error";
            StatusText = Loc.T("StatusErrorShort");
        }
        else if (paused)
        {
            StatusKind = "Off";
            StatusText = Loc.T("StatusPausedLong");
        }
        else if (active > 0)
        {
            StatusKind = "Ok";
            StatusText = Loc.T("StatusRunningShort", Loc.Plural("PluralActiveRules", active));
        }
        else
        {
            StatusKind = views.Any(v => v.State is RuleState.Waiting or RuleState.Blocking) ? "Warn" : "Off";
            StatusText = views.Count == 0 ? Loc.T("StatusNoRulesYet") : status;
        }

        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(HasRules));
    }

    private void OnTick()
    {
        _tick++;
        double down = 0, up = 0;
        foreach (var r in Rules)
        {
            r.Sample();
            down += r.CurrentDown;
            up += r.CurrentUp;
        }
        TotalSpeed = Rules.Count == 0 ? "" : $"↓ {Format.Speed(down)}   ↑ {Format.Speed(up)}";

        SampleAdapters();
        if (_tick % 3 == 1)
            CountProcesses();
        if (_tick % 60 == 0)
            Task.Run(_engine.PruneCaches);
    }

    private void SampleAdapters()
    {
        if (Interlocked.Exchange(ref _samplingAdapters, 1) == 1)
            return;
        var ids = Adapters.Select(a => a.Id).ToHashSet();
        Task.Run(() =>
        {
            var stats = new Dictionary<string, (long Rx, long Tx)>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!ids.Contains(nic.Id))
                        continue;
                    var s = nic.GetIPStatistics();
                    stats[nic.Id] = (s.BytesReceived, s.BytesSent);
                }
            }
            catch (NetworkInformationException) { }
            return stats;
        }).ContinueWith(t =>
        {
            Interlocked.Exchange(ref _samplingAdapters, 0);
            if (!t.IsCompletedSuccessfully)
                return;
            _dispatcher.BeginInvoke(() =>
            {
                foreach (var chip in Adapters)
                    if (t.Result.TryGetValue(chip.Id, out var s))
                        chip.Sample(s.Rx, s.Tx);
            });
        });
    }

    private void CountProcesses()
    {
        var rules = Rules.Select(r => r.Rule).ToList();
        Task.Run(() => rules.ToDictionary(r => r.Id, r => _engine.FindProcesses(r).Count))
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully)
                    return;
                _dispatcher.BeginInvoke(() =>
                {
                    foreach (var card in Rules)
                        if (t.Result.TryGetValue(card.Id, out int n))
                            card.Processes = n;
                });
            });
    }

    private void OnRuleToggled(RuleCardViewModel card)
    {
        Logger.Write(Loc.T(card.IsEnabled ? "LogRuleEnabled" : "LogRuleDisabled", card.Name));
        _engine.SaveAndApply();
    }

    private void AddRule()
    {
        _engine.RefreshAdaptersNow();
        var result = EditRule?.Invoke(new Rule(), true);
        if (result == null)
            return;
        lock (_engine.Settings)
            _engine.Settings.Rules.Add(result);
        Logger.Write(Loc.T("LogRuleAdded", result.ProgramPath, result.AdapterName));
        _engine.SaveAndApply();
    }

    private void EditRuleCard(RuleCardViewModel? card)
    {
        if (card == null)
            return;
        _engine.RefreshAdaptersNow();
        var result = EditRule?.Invoke(card.Rule.Clone(), false);
        if (result == null)
            return;
        lock (_engine.Settings)
        {
            int idx = _engine.Settings.Rules.FindIndex(r => r.Id == card.Id);
            if (idx >= 0)
                _engine.Settings.Rules[idx] = result;
        }
        Logger.Write(Loc.T("LogRuleChanged", result.ProgramPath, result.AdapterName));
        _engine.SaveAndApply();
    }

    private void DeleteRule(RuleCardViewModel? card)
    {
        if (card == null)
            return;
        if (Confirm != null && !Confirm(Loc.T("ConfirmDeleteTitle"),
                Loc.T("ConfirmDeleteText", card.Name), Loc.T("Delete")))
            return;
        lock (_engine.Settings)
            _engine.Settings.Rules.RemoveAll(r => r.Id == card.Id);
        Logger.Write(Loc.T("LogRuleDeleted", card.Rule.ProgramPath));
        _engine.SaveAndApply();
    }

    private void TogglePause()
    {
        _engine.Settings.Paused = !_engine.Settings.Paused;
        Logger.Write(Loc.T(_engine.Settings.Paused ? "LogAllPaused" : "LogResumed"));
        OnPropertyChanged(nameof(IsPaused));
        _engine.SaveAndApply();
    }

    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(SettingsStore.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.Directory}\"") { UseShellExecute = true });
    }

    public void Dispose()
    {
        _timer.Stop();
        Logger.Written -= OnLogWritten;
        _engine.Changed -= OnEngineChanged;
        Loc.Changed -= OnLanguageChanged;
    }
}
