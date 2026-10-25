using AppEgress.Localization;

namespace AppEgress.UI.ViewModels;

/// <summary>Сегмент переключателя языка в настройках: «Авто (English)», «English», «Русский».</summary>
internal sealed class LanguageOptionViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly string? _nativeName;

    public LanguageOptionViewModel(MainViewModel owner, string code, string? nativeName)
    {
        _owner = owner;
        Code = code;
        _nativeName = nativeName;
    }

    public string Code { get; }

    /// <summary>Языки подписаны самоназванием и не переводятся; «Авто» — на текущем языке.</summary>
    public string Label => _nativeName ?? _owner.AutoLanguageLabel;

    public bool IsSelected
    {
        get => _owner.LanguagePreference == Code;
        set
        {
            if (value)
                _owner.LanguagePreference = Code;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsSelected));
    }
}
