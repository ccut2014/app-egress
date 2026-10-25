using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace AppEgress.Config;

/// <summary>
/// Автозапуск через Планировщик заданий с наивысшими правами: ключ Run в реестре
/// не может без запроса UAC запустить программу, которой нужны права администратора.
/// </summary>
internal static class Autostart
{
    private const string TaskName = AppIdentity.AutostartTask;

    public static bool IsEnabled()
    {
        if (RunSchtasks(["/Query", "/TN", TaskName, "/XML"], out _, out string xml) != 0)
            return false;
        try
        {
            var task = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            string? current = Environment.ProcessPath;
            bool disabled = task.Descendants(ns + "Settings").Elements(ns + "Enabled")
                .Any(e => string.Equals(e.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
            return !disabled && current != null && task.Descendants(ns + "Command")
                .Any(e => string.Equals(e.Value.Trim().Trim('"'), current, StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Xml.XmlException) { return false; }
    }

    public static bool Enable(out string error)
    {
        string exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
        return RunSchtasks(
            ["/Create", "/TN", TaskName, "/TR", $"\"{exe}\" --minimized", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"],
            out error) == 0;
    }

    public static bool Disable(out string error) => RunSchtasks(["/Delete", "/TN", TaskName, "/F"], out error) == 0;

    private static int RunSchtasks(string[] args, out string error)
        => RunSchtasks(args, out error, out _);

    private static int RunSchtasks(string[] args, out string error, out string output)
    {
        output = "";
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
            var stderr = p.StandardError.ReadToEndAsync();
            using var stdoutBytes = new MemoryStream();
            var stdout = p.StandardOutput.BaseStream.CopyToAsync(stdoutBytes);
            if (!p.WaitForExit(10000))
                throw new TimeoutException("Task Scheduler did not respond within 10 seconds.");
            error = stderr.GetAwaiter().GetResult().Trim();
            stdout.GetAwaiter().GetResult();
            byte[] bytes = stdoutBytes.ToArray();
            // schtasks XML can be UTF-16 even when the parent process uses a UTF-8 console.
            Encoding encoding = p.StandardOutput.CurrentEncoding;
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
                encoding = Encoding.Unicode;
            else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
                encoding = Encoding.BigEndianUnicode;
            else if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0)
                encoding = Encoding.Unicode;
            output = encoding.GetString(bytes).TrimStart('\ufeff');
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }
}
