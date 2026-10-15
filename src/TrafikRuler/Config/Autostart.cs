using System.Diagnostics;

namespace TrafikRuler.Config;

/// <summary>
/// Автозапуск через Планировщик заданий с наивысшими правами: ключ Run в реестре
/// не может без запроса UAC запустить программу, которой нужны права администратора.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "TrafikRuler";

    public static bool IsEnabled() => RunSchtasks(["/Query", "/TN", TaskName], out _) == 0;

    public static bool Enable(out string error)
    {
        string exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
        return RunSchtasks(
            ["/Create", "/TN", TaskName, "/TR", $"\"{exe}\" --minimized", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"],
            out error) == 0;
    }

    public static bool Disable(out string error) => RunSchtasks(["/Delete", "/TN", TaskName, "/F"], out error) == 0;

    private static int RunSchtasks(string[] args, out string error)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            error = p.StandardError.ReadToEnd().Trim();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }
}
