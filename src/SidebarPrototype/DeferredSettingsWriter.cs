using System.Text.Json;
using System.Windows.Threading;

namespace SidebarPrototype;

/// <summary>Snapshot on the UI thread, then serialize disk writes off that thread in request order.</summary>
internal sealed class DeferredSettingsWriter
{
    private readonly AppSettings _settings;
    private readonly string _path;
    private readonly DispatcherTimer _timer;
    private Task _tail = Task.CompletedTask;
    public DeferredSettingsWriter(AppSettings settings, string path)
    {
        _settings = settings; _path = path;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => { _timer.Stop(); EnqueueSnapshot(); };
    }
    public void Schedule() { _timer.Stop(); _timer.Start(); }
    private void EnqueueSnapshot()
    {
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        _tail = _tail.ContinueWith(async _ => await snapshot.SaveAtomicAsync(_path),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
    public Task FlushAsync() { _timer.Stop(); EnqueueSnapshot(); return _tail; }
}
