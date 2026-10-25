using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AppEgress.UI.ViewModels;
using AppEgress.Localization;

namespace AppEgress.UI;

internal partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>true — окно действительно закрывается (выход из программы), а не прячется в трей.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Окно спрятано в трей крестиком.</summary>
    public event Action? HiddenToTray;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WindowEffects.Prepare(this, Backdrop.Mica, TitleBar);

        // Иконка окна и панели задач берётся из exe (ApplicationIcon). В заголовок ставим
        // кадр нужного размера, чтобы он был чётким на любом масштабе экрана.
        TitleIcon.Source = AppIcons.GetImageSource(IconState.Active, 16);
        Loaded += (_, _) =>
        {
            double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            TitleIcon.Source = AppIcons.GetImageSource(IconState.Active, (int)Math.Round(16 * scale));
        };

        vm.EditRule = (rule, isNew) => RuleWindow.Edit(this, rule, isNew);
        vm.Confirm = (title, text, primary) => ConfirmWindow.Ask(this, title, text, primary);

        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // Крестик прячет окно в трей, правила продолжают работать.
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }
        base.OnClosing(e);
    }

    private void OnCardMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        for (DependencyObject? source = e.OriginalSource as DependencyObject; source != null;
             source = source is System.Windows.Media.Visual
                 ? System.Windows.Media.VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if (source is Expander) return;
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: RuleCardViewModel card })
        {
            e.Handled = true;
            _vm.EditRuleCommand.Execute(card);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RuleCardViewModel card } button)
            return;
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
        };
        menu.Items.Add(MenuItem(Loc.T("Edit"), Glyphs.Edit, () => _vm.EditRuleCommand.Execute(card)));
        menu.Items.Add(MenuItem(Loc.T(card.IsEnabled ? "TurnOff" : "TurnOn"), card.IsEnabled ? Glyphs.Pause : Glyphs.Play,
            () => card.IsEnabled = !card.IsEnabled));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Loc.T("Delete"), Glyphs.Delete, () => _vm.DeleteRuleCommand.Execute(card)));
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, string glyph, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontSize = 14 },
        };
        item.Click += (_, _) => action();
        return item;
    }
}
