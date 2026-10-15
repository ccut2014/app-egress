using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TrafikRuler.Localization;

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

/// <summary>Размеры и скорости с единицами текущего языка (КБ/KB, МБ/с/MB/s) и его разделителем дробной части.</summary>
internal static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => Loc.T("SizeB", bytes),
        < 1024 * 1024 => Loc.T("SizeKB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => Loc.T("SizeMB", bytes / (1024.0 * 1024)),
        _ => Loc.T("SizeGB", bytes / (1024.0 * 1024 * 1024)),
    };

    public static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1 => Loc.T("SpeedB", 0),
        < 1024 => Loc.T("SpeedB", bytesPerSecond),
        < 1024 * 1024 => Loc.T("SpeedKB", bytesPerSecond / 1024),
        _ => Loc.T("SpeedMB", bytesPerSecond / (1024 * 1024)),
    };
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
