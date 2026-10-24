using System.Collections.Generic;

namespace SidebarPrototype.Shell;

/// <summary>Foreground-window helpers. This class observes and restores focus; it never consumes input.</summary>
public static class ForegroundWindowHelper
{
    public static nint Capture() => NativeMethods.GetForegroundWindow();
    public static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public static void Restore(nint hwnd)
    {
        if (hwnd != 0 && NativeMethods.IsWindow(hwnd)) NativeMethods.SetForegroundWindow(hwnd);
    }

    public static bool IsOwnedByCurrentProcess(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return false;
        var visited = new HashSet<nint>();
        var pending = new Queue<nint>();
        pending.Enqueue(hwnd);
        var inspected = 0;
        while (pending.Count > 0 && inspected++ < 32)
        {
            var current = pending.Dequeue();
            if (current == 0 || !visited.Add(current) || !NativeMethods.IsWindow(current)) continue;
            NativeMethods.GetWindowThreadProcessId(current, out var processId);
            if (processId == (uint)Environment.ProcessId) return true;

            var rootOwner = NativeMethods.GetAncestor(current, NativeMethods.GaRootOwner);
            if (rootOwner != 0 && !visited.Contains(rootOwner)) pending.Enqueue(rootOwner);
            var owner = NativeMethods.GetWindow(current, NativeMethods.GwOwner);
            if (owner != 0 && !visited.Contains(owner)) pending.Enqueue(owner);
        }
        return false;
    }
}
