using System.Diagnostics;

namespace Eden.UwpPorting.Core;

public static class BootDiagnostics
{
    public static void Info(string message)
    {
        Debug.WriteLine($"[HENIX][INFO] {message}");
    }

    public static void Warn(string message)
    {
        Debug.WriteLine($"[HENIX][WARN] {message}");
    }

    public static void Error(string message)
    {
        Debug.WriteLine($"[HENIX][ERROR] {message}");
    }
}
