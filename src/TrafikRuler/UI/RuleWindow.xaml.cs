using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TrafikRuler.Core;

namespace TrafikRuler.UI;

/// <summary>Создание и изменение правила: программа, адаптер, kill-switch.</summary>
internal partial class RuleWindow : Window
{
    internal sealed record AdapterOption(AdapterInfo Info, string Glyph, string Title, string Kind, string Detail, bool IsUsable);

    private readonly Rule _rule;
    private readonly List<AdapterOption> _options = new();
    private bool _syncing;

    public RuleWindow(Rule rule, IReadOnlyList<AdapterInfo> adapters, bool isNew)
    {
        InitializeComponent();
        WindowEffects.Prepare(this, Backdrop.Mica, TitleBar);
        // На небольших экранах окно не должно вылезать за край: содержимое прокручивается.
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        _rule = rule;

        Title = isNew ? "Новое правило" : "Изменить правило";
        Heading.Text = Title;
        SaveButton.Content = isNew ? "Добавить" : "Сохранить";

        foreach (var a in adapters)
            _options.Add(Describe(a));
        if (!string.IsNullOrEmpty(rule.AdapterId) && adapters.All(a => a.Id != rule.AdapterId))
        {
            // Адаптер из правила сейчас не найден (например, Wi-Fi выключен): оставляем его в списке.
            var missing = new AdapterInfo { Id = rule.AdapterId, Name = rule.AdapterName, Description = "сейчас не найден" };
            _options.Add(new AdapterOption(missing, Glyphs.Globe, rule.AdapterName, "", "Сейчас не найден в системе", false));
        }
        AdapterList.ItemsSource = _options;

        int sel = _options.FindIndex(o => o.Info.Id == rule.AdapterId);
        if (sel < 0)
            sel = _options.FindIndex(o => o.IsUsable && o.Info.Kind == AdapterKind.WiFi);
        if (sel < 0)
            sel = _options.FindIndex(o => o.IsUsable);
        AdapterList.SelectedIndex = sel;

        _syncing = true;
        PathBox.Text = rule.ProgramPath;
        ByName.IsChecked = rule.ProgramPath.Length > 0 && rule.MatchesByNameOnly;
        _syncing = false;
        KillSwitch.IsChecked = rule.KillSwitch;
        EnabledToggle.IsChecked = rule.Enabled;
        UpdateProgramPreview();
        UpdateHint();
    }

    /// <summary>Показывает окно. Возвращает изменённое правило или null, если нажали «Отмена».</summary>
    public static Rule? Edit(Window owner, Rule rule, bool isNew)
    {
        var w = new RuleWindow(rule, AdapterService.GetAll(), isNew) { Owner = owner };
        return w.ShowDialog() == true ? w._rule : null;
    }

    private static AdapterOption Describe(AdapterInfo a)
    {
        string ip = a.IPv4 is { } v4 ? Addr.Format(v4) : "нет IPv4";
        string detail = a.IsUp ? $"{ip} · {a.Description}" : $"отключён · {a.Description}";
        return new AdapterOption(a, Glyphs.ForAdapter(a.Kind), a.Name, a.KindLabel, detail, a.IsUsable);
    }

    private void OnPathChanged(object sender, TextChangedEventArgs e) => UpdateProgramPreview();

    private void OnByNameChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        if (ByName.IsChecked == true)
            PathBox.Text = Path.GetFileName(PathBox.Text.Trim().Trim('"'));
        UpdateProgramPreview();
    }

    private void UpdateProgramPreview()
    {
        string path = PathBox.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            ProgramName.Text = "Программа не выбрана";
            ProgramDetail.Text = "Выберите её среди запущенных или укажите exe";
            ProgramIcon.Source = null;
            ProgramGlyph.Visibility = Visibility.Visible;
            return;
        }
        bool byName = path.IndexOfAny(['\\', '/']) < 0;
        ProgramName.Text = Path.GetFileName(path);
        ProgramDetail.Text = byName ? "Любой exe с таким именем" : path;
        var icon = byName ? null : IconCache.Get(path);
        ProgramIcon.Source = icon;
        ProgramGlyph.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        var a = (AdapterList.SelectedItem as AdapterOption)?.Info;
        string hint = a switch
        {
            null => "Выберите адаптер.",
            { IsUp: false } => "Адаптер сейчас отключён. Правило заработает, когда он подключится.",
            { IPv4: null } => "У адаптера нет IPv4-адреса. Правило заработает, когда адрес появится.",
            { HasGateway: false, Kind: not AdapterKind.Vpn } => "У адаптера нет шлюза по умолчанию: интернет через него может не работать.",
            { IPv6: null } => "У адаптера нет IPv6: IPv6-соединения программы будут отклоняться, и она перейдёт на IPv4.",
            _ => "",
        };
        HintText.Text = hint;
        HintBar.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPickRunning(object sender, RoutedEventArgs e)
    {
        var path = ProcessPickerWindow.Pick(this);
        if (path == null)
            return;
        _syncing = true;
        ByName.IsChecked = false;
        _syncing = false;
        PathBox.Text = path;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            Title = "Выберите программу",
        };
        string current = PathBox.Text.Trim().Trim('"');
        if (File.Exists(current))
        {
            dlg.InitialDirectory = Path.GetDirectoryName(current);
            dlg.FileName = Path.GetFileName(current);
        }
        if (dlg.ShowDialog(this) != true)
            return;
        _syncing = true;
        ByName.IsChecked = false;
        _syncing = false;
        PathBox.Text = dlg.FileName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string path = PathBox.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            ConfirmWindow.Info(this, "Не выбрана программа", "Выберите программу из запущенных, через «Обзор…» или впишите имя exe.");
            return;
        }
        bool byName = ByName.IsChecked == true || path.IndexOfAny(['\\', '/']) < 0;
        if (!byName && !File.Exists(path)
            && !ConfirmWindow.Ask(this, "Файл не найден", $"«{path}» не существует. Всё равно сохранить правило?", "Сохранить"))
            return;
        if (AdapterList.SelectedItem is not AdapterOption option)
        {
            ConfirmWindow.Info(this, "Не выбран адаптер", "Выберите адаптер, через который пойдёт трафик программы.");
            return;
        }

        _rule.ProgramPath = byName ? Path.GetFileName(path) : path;
        _rule.AdapterId = option.Info.Id;
        _rule.AdapterName = option.Info.Name;
        _rule.KillSwitch = KillSwitch.IsChecked == true;
        _rule.Enabled = EnabledToggle.IsChecked == true;
        DialogResult = true;
    }
}
