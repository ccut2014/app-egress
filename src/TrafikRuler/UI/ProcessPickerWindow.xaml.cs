using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrafikRuler.Native;

namespace TrafikRuler.UI;

/// <summary>Выбор программы среди запущенных процессов (одна строка на exe).</summary>
internal partial class ProcessPickerWindow : Window
{
    internal sealed class Entry
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public List<int> Pids { get; } = new();
        public string? WindowTitle { get; set; }
        public ImageSource? Icon { get; set; }
        public string PidText => Pids.Count == 1 ? $"PID {Pids[0]}" : $"{Pids.Count} процессов";
    }

    private List<Entry> _entries = new();

    public string? SelectedPath { get; private set; }

    public ProcessPickerWindow()
    {
        InitializeComponent();
        WindowEffects.Prepare(this, Backdrop.Mica, TitleBar);
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            LoadProcesses();
        };
    }

    public static string? Pick(Window owner)
    {
        var w = new ProcessPickerWindow { Owner = owner };
        return w.ShowDialog() == true ? w.SelectedPath : null;
    }

    private async void LoadProcesses()
    {
        LoadingText.Visibility = Visibility.Visible;
        _entries = await Task.Run(Collect);
        Populate();
        LoadingText.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static List<Entry> Collect()
    {
        var byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == self || p.Id <= 4)
                    continue;
                string? path = ProcessNative.GetImagePath((uint)p.Id);
                if (path == null)
                    continue;
                string? title = null;
                try
                {
                    title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : p.MainWindowTitle;
                }
                catch (InvalidOperationException) { }
                if (!byPath.TryGetValue(path, out var e))
                    byPath[path] = e = new Entry { Name = System.IO.Path.GetFileName(path), Path = path };
                e.WindowTitle ??= title;
                e.Pids.Add(p.Id);
            }
        }
        // Иконки замороженные (Freeze), поэтому их можно готовить в фоновом потоке.
        foreach (var e in byPath.Values)
            e.Icon = IconCache.Get(e.Path);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // Программы с окнами наверху, системные процессы ниже.
        return byPath.Values
            .OrderBy(e => e.WindowTitle == null)
            .ThenBy(e => e.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Populate()
    {
        string f = SearchBox.Text.Trim();
        List.ItemsSource = f.Length == 0
            ? _entries
            : _entries.Where(e => e.Path.Contains(f, StringComparison.OrdinalIgnoreCase)
                                  || (e.WindowTitle?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (List.Items.Count > 0)
            List.SelectedIndex = 0;
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Populate();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (List.Items.Count == 0)
            return;
        if (e.Key == Key.Down)
        {
            List.SelectedIndex = Math.Min(List.Items.Count - 1, List.SelectedIndex + 1);
            List.ScrollIntoView(List.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            List.SelectedIndex = Math.Max(0, List.SelectedIndex - 1);
            List.ScrollIntoView(List.SelectedItem);
            e.Handled = true;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadProcesses();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void OnSelect(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (List.SelectedItem is not Entry entry)
            return;
        SelectedPath = entry.Path;
        DialogResult = true;
    }
}
