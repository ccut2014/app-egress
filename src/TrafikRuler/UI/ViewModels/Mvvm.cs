using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace TrafikRuler.UI.ViewModels;

internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

internal static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} МБ",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} ГБ",
    };

    public static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1 => "0 Б/с",
        < 1024 => $"{bytesPerSecond:0} Б/с",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:0.#} КБ/с",
        _ => $"{bytesPerSecond / (1024 * 1024):0.#} МБ/с",
    };

    /// <summary>Склонение: 1 процесс, 2 процесса, 5 процессов.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        long n100 = Math.Abs(n) % 100, n10 = n100 % 10;
        string word = n100 is >= 11 and <= 14 ? many : n10 == 1 ? one : n10 is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }
}

/// <summary>Кольцевой буфер скоростей за последнюю минуту.</summary>
internal sealed class SpeedHistory
{
    public const int Length = 60;
    private readonly double[] _values = new double[Length];
    private int _head;

    public void Push(double value)
    {
        _values[_head] = value;
        _head = (_head + 1) % Length;
    }

    public double[] ToArray()
    {
        var result = new double[Length];
        for (int i = 0; i < Length; i++)
            result[i] = _values[(_head + i) % Length];
        return result;
    }
}
