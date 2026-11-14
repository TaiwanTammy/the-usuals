using System.Diagnostics;
using System.Security.Cryptography;

namespace SidebarPrototype;

// When running from source, the copy in use lives in app\ and every new build lands in build\publish beside it.
// Quitting and reopening (or Restart in settings) swaps the new build in, so nobody has to paste copy commands.
// A downloaded copy has no build\publish folder next to it, so none of this does anything there.
internal static class LocalUpdate
{
    private static string AppDir => AppContext.BaseDirectory.TrimEnd('\\', '/');

    private static string? Candidate
    {
        get
        {
            var dir = Path.GetFullPath(Path.Combine(AppDir, "..", "build", "publish"));
            return !dir.Equals(AppDir, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, "SidebarPrototype.dll")) ? dir : null;
        }
    }

    private static string? Hash(string file)
    {
        try { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        catch { return null; }
    }

    private static string AttemptFile(string dataRoot) => Path.Combine(dataRoot, "update-attempt.txt");

    // A newer build is waiting, and copying that exact build hasn't already been tried (so a failed copy never loops).
    public static bool Waiting(string dataRoot)
    {
        if (Candidate is not { } dir || Hash(Path.Combine(dir, "SidebarPrototype.dll")) is not { } next) return false;
        if (next == Hash(Path.Combine(AppDir, "SidebarPrototype.dll"))) return false;
        try { return File.ReadAllText(AttemptFile(dataRoot)).Trim() != next; } catch { return true; }
    }

    // Starts a hidden script that waits for this copy to finish quitting, closes the Chrome relay copies (they keep
    // the files open), copies in the waiting build if asked, then opens the sidebar again.
    public static void RestartAfterExit(string dataRoot, bool update)
    {
        var pid = Environment.ProcessId;
        var lines = new List<string>
        {
            "@echo off",
            ":wait",
            $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)",
        };
        if (update && Candidate is { } dir)
        {
            try { File.WriteAllText(AttemptFile(dataRoot), Hash(Path.Combine(dir, "SidebarPrototype.dll")) ?? ""); } catch { }
            lines.Add("taskkill /im SidebarPrototype.exe /f >nul 2>&1");
            lines.Add("timeout /t 1 /nobreak >nul");
            lines.Add($"robocopy \"{dir}\" \"{AppDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
        }
        lines.Add($"start \"\" \"{Path.Combine(AppDir, "SidebarPrototype.exe")}\"");
        lines.Add("del \"%~f0\"");
        var script = Path.Combine(Path.GetTempPath(), $"usuals-restart-{pid}.cmd");
        File.WriteAllText(script, string.Join("\r\n", lines));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }
}
