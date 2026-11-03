using System.Diagnostics;

namespace TimeTracker;

/// <summary>
/// macOS backend. .NET has no CoreBluetooth binding, so this drives the small Swift helper in mac/zei-ble.swift
/// (built by mac/build.sh, placed next to the app) and reads its stdout. macOS hides Bluetooth addresses, so the
/// helper finds the ZEI itself and the device handle here is just a marker.
/// </summary>
public class MacZeiLink : IZeiLink
{
    static readonly object Marker = new();

    static string HelperPath()
    {
        var p = Environment.GetEnvironmentVariable("TT_ZEI_HELPER") ?? Path.Combine(AppContext.BaseDirectory, "zei-ble");
        return File.Exists(p) ? p : throw new Exception($"Bluetooth helper not found at {p} (build it with mac/build.sh)");
    }

    static Process Start(string mode)
    {
        var psi = new ProcessStartInfo(HelperPath(), mode)
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true, // held open; the helper exits when we close it or die
            UseShellExecute = false,
        };
        return Process.Start(psi) ?? throw new Exception("Could not start the Bluetooth helper");
    }

    public Task<object> FindDevice(CancellationToken ct)
    {
        HelperPath(); // fail early with a clear message
        return Task.FromResult(Marker);
    }

    public async Task<IZeiConnection> Connect(object device, CancellationToken ct)
    {
        var conn = new Conn(Start("connect"));
        try
        {
            using var reg = ct.Register(conn.Dispose);
            var done = await Task.WhenAny(conn.Ready, Task.Delay(TimeSpan.FromSeconds(30), ct));
            ct.ThrowIfCancellationRequested();
            if (done != conn.Ready) throw new Exception("Timed out connecting to ZEI");
            await conn.Ready; // rethrows the helper's ERROR
            return conn;
        }
        catch { conn.Dispose(); throw; }
    }

    public async Task WaitForWake(object device, CancellationToken ct)
    {
        using var p = Start("wake");
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            if (line == "WOKE") return;
            if (line.StartsWith("ERROR ")) throw new Exception(line[6..]);
        }
        ct.ThrowIfCancellationRequested();
        throw new Exception("Bluetooth helper exited");
    }

    sealed class Conn : IZeiConnection
    {
        readonly Process proc;
        readonly TaskCompletionSource ready = new();
        readonly TaskCompletionSource gone = new();
        int last = -1;

        public Task Ready => ready.Task;
        public Task Disconnected => gone.Task;
        public event Action<int>? SideChanged;

        public Conn(Process proc)
        {
            this.proc = proc;
            _ = Pump();
        }

        async Task Pump()
        {
            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    if (line == "CONNECTED") ready.TrySetResult();
                    else if (line.StartsWith("SIDE ") && int.TryParse(line.AsSpan(5), out var s)) { last = s; SideChanged?.Invoke(s); }
                    else if (line.StartsWith("ERROR ")) ready.TrySetException(new Exception(line[6..]));
                }
            }
            catch { }
            ready.TrySetException(new Exception("Bluetooth helper exited"));
            gone.TrySetResult();
        }

        // The helper polls the ZEI itself and reports every update, so just hand back the latest.
        public Task<int?> ReadSide() => Task.FromResult<int?>(last >= 0 ? last : null);

        public void Dispose()
        {
            try { proc.Kill(); } catch { }
            proc.Dispose();
            gone.TrySetResult();
        }
    }
}
