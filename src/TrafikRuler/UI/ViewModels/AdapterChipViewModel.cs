using TrafikRuler.Core;

namespace TrafikRuler.UI.ViewModels;

/// <summary>Чип адаптера в нижней панели: имя, адрес и текущая скорость интерфейса.</summary>
internal sealed class AdapterChipViewModel : ObservableObject
{
    private long _lastRx = -1, _lastTx = -1;
    private string _speedText = "";
    private bool _isUsed;
    private AdapterInfo _info;

    public AdapterChipViewModel(AdapterInfo info)
    {
        _info = info;
    }

    public string Id => _info.Id;
    public string Name => _info.Name;
    public string Glyph => Glyphs.ForAdapter(_info.Kind);
    public string KindLabel => _info.KindLabel;
    public string IpText => _info.IPv4 is { } a ? Addr.Format(a) : "нет IPv4";
    public string ToolTipText => $"{_info.Description}\n{KindLabel} · {IpText}" + (_info.IPv6 is { } v6 ? $"\n{Addr.Format(v6)}" : "");

    /// <summary>Через этот адаптер идёт хотя бы одно активное правило.</summary>
    public bool IsUsed { get => _isUsed; set => Set(ref _isUsed, value); }

    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }

    public void Update(AdapterInfo info)
    {
        _info = info;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(IpText));
        OnPropertyChanged(nameof(ToolTipText));
    }

    public void Sample(long rx, long tx)
    {
        if (_lastRx >= 0)
            SpeedText = $"↓ {Format.Speed(Math.Max(0, rx - _lastRx))}   ↑ {Format.Speed(Math.Max(0, tx - _lastTx))}";
        _lastRx = rx;
        _lastTx = tx;
    }
}
