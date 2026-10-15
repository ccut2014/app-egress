using System.Windows;
using System.Windows.Controls;
using TrafikRuler.Localization;

namespace TrafikRuler.UI;

/// <summary>Короткий диалог в стиле Fluent вместо MessageBox.</summary>
internal partial class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message, string primary, string? close)
    {
        InitializeComponent();
        WindowEffects.Prepare(this, Backdrop.Mica, TitleBar);
        Heading.Text = title;
        Message.Text = message;
        PrimaryButton.Content = primary;
        if (close == null)
        {
            CloseButton.Visibility = Visibility.Collapsed;
            Grid.SetColumnSpan(PrimaryButton, 3);
        }
        else
        {
            CloseButton.Content = close;
        }
    }

    public static bool Ask(Window? owner, string title, string message, string primary)
    {
        var w = new ConfirmWindow(title, message, primary, Loc.T("Cancel"));
        if (owner is { IsVisible: true })
            w.Owner = owner;
        else
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message)
    {
        var w = new ConfirmWindow(title, message, Loc.T("GotIt"), null);
        if (owner is { IsVisible: true })
            w.Owner = owner;
        else
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => DialogResult = true;
}
