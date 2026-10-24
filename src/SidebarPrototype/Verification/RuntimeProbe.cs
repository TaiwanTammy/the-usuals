using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace SidebarPrototype.Verification;

public sealed record WorkAreaSnapshot(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed record ProcessMetrics(int SampledProcesses, long SumWorkingSetBytes, long SumPrivateBytes,
    double SampleMilliseconds, double CpuCorePercent, double MachineCpuPercent);

/// <summary>Diagnostic evidence only; never enumerates unrelated browser sessions or page URLs.</summary>
public static class RuntimeProbe
{
    public static WorkAreaSnapshot CaptureWorkArea()
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromPoint(new Point(0, 0), 1);
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Monitor work-area query failed.");
        return new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
    }

    public static async Task<ProcessMetrics> MeasureAsync(CoreWebView2Environment environment, int milliseconds = 2000)
    {
        var pids = environment.GetProcessInfos().Select(x => x.ProcessId).Append(Environment.ProcessId).Distinct().ToArray();
        var start = Read(pids); var timer = Stopwatch.StartNew();
        await Task.Delay(milliseconds);
        var end = Read(pids); timer.Stop();
        var deltaCpu = end.Where(x => start.ContainsKey(x.Key)).Sum(x => Math.Max(0, x.Value.Cpu - start[x.Key].Cpu));
        var corePercent = timer.Elapsed.TotalMilliseconds > 0 ? deltaCpu / timer.Elapsed.TotalMilliseconds * 100 : 0;
        return new(end.Count, end.Sum(x => x.Value.Working), end.Sum(x => x.Value.Private),
            timer.Elapsed.TotalMilliseconds, corePercent, corePercent / Environment.ProcessorCount);
    }

    private static Dictionary<int, (double Cpu, long Working, long Private)> Read(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, (double, long, long)>();
        foreach (var id in pids) {
            try { using var process = Process.GetProcessById(id); result[id] = (process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64, process.PrivateMemorySize64); }
            catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
        return result;
    }

    public static byte[] CreateAudioWave()
    {
        const int sampleRate = 22050, samples = sampleRate * 30, dataLength = samples * 2;
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write("RIFF"u8); writer.Write(36 + dataLength); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataLength);
        for (var i = 0; i < samples; i++) writer.Write((short)(200 * Math.Sin(2 * Math.PI * 440 * i / sampleRate)));
        return memory.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
