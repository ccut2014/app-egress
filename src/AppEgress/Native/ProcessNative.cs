using System.Runtime.InteropServices;
using System.Text;

namespace AppEgress.Native;

internal static class ProcessNative
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    /// <summary>
    /// Возвращает время создания процесса (FILETIME) и, если нужно, полный путь к его exe.
    /// Время создания позволяет заметить, что PID достался новому процессу.
    /// </summary>
    public static bool TryQuery(uint pid, bool wantPath, out long creationTime, out string? path)
    {
        creationTime = 0;
        path = null;
        if (pid == 0 || pid == 4)
            return false;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return false;
        try
        {
            if (!GetProcessTimes(h, out creationTime, out _, out _, out _))
                return false;
            if (wantPath)
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                    path = sb.ToString(0, (int)size);
            }
            return true;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static string? GetImagePath(uint pid) =>
        TryQuery(pid, wantPath: true, out _, out string? path) ? path : null;
}
