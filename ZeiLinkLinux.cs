using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;

namespace TimeTracker;

/// <summary>Linux backend: BlueZ over D-Bus (needs the bluetooth service running). The device handle is the BlueZ <see cref="Device"/>.</summary>
public class LinuxZeiLink : IZeiLink
{
    static readonly string ServiceId = ZeiLinks.ServiceUuid.ToString();
    static readonly string OrientationId = ZeiLinks.OrientationUuid.ToString();
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    static async Task<Adapter> GetAdapter() =>
        (await BlueZManager.GetAdaptersAsync()).FirstOrDefault()
        ?? throw new Exception("No Bluetooth adapter found (is the bluetooth service running?)");

    static async Task<bool> IsZei(Device d)
    {
        var name = await d.GetAliasAsync() ?? "";
        if (ZeiLinks.IsZeiName(name)) return true;
        var uuids = await d.GetUUIDsAsync();
        return uuids != null && uuids.Contains(ServiceId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<object> FindDevice(CancellationToken ct)
    {
        var adapter = await GetAdapter();

        // BlueZ remembers devices it has seen or paired, so check there before scanning.
        foreach (var d in await adapter.GetDevicesAsync())
            if (await IsZei(d)) return d;

        var found = new TaskCompletionSource<Device>();
        using var watch = await adapter.WatchDevicesAddedAsync(async dev =>
        {
            if (await IsZei(dev)) found.TrySetResult(dev);
        });
        await adapter.StartDiscoveryAsync();
        try
        {
            using var reg = ct.Register(() => found.TrySetCanceled());
            var done = await Task.WhenAny(found.Task, Task.Delay(Timeout, ct));
            if (done != found.Task) throw new Exception("ZEI not found nearby");
            return await found.Task;
        }
        finally { await adapter.StopDiscoveryAsync(); }
    }

    public async Task<IZeiConnection> Connect(object device, CancellationToken ct)
    {
        var dev = (Device)device;
        await dev.ConnectAsync();
        await dev.WaitForPropertyValueAsync("ServicesResolved", value: true, Timeout);
        try { return await Conn.Open(dev); }
        catch { try { await dev.DisconnectAsync(); } catch { } throw; }
    }

    public async Task WaitForWake(object device, CancellationToken ct)
    {
        // BlueZ only exposes a device's RSSI while it is being heard, and drops it after a quiet spell.
        // So with discovery on, the ZEI waking up shows as its RSSI property reappearing.
        var dev = (Device)device;
        var adapter = await GetAdapter();
        await adapter.StartDiscoveryAsync();
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                try { await dev.GetRSSIAsync(); return; }
                catch (Exception e) when (e is not OperationCanceledException) { } // no RSSI yet: still asleep
            }
        }
        finally { await adapter.StopDiscoveryAsync(); }
    }

    sealed class Conn : IZeiConnection
    {
        readonly Device device;
        readonly GattCharacteristic ch;
        readonly TaskCompletionSource gone = new();

        public Task Disconnected => gone.Task;
        public event Action<int>? SideChanged;

        Conn(Device device, GattCharacteristic ch) { this.device = device; this.ch = ch; }

        public static async Task<Conn> Open(Device device)
        {
            var svc = await device.GetServiceAsync(ServiceId)
                      ?? throw new Exception("ZEI service not found. Try pairing it first: bluetoothctl pair <address>");
            var ch = await svc.GetCharacteristicAsync(OrientationId)
                     ?? throw new Exception("Orientation characteristic not found");
            var c = new Conn(device, ch);
            device.Disconnected += (_, _) => { c.gone.TrySetResult(); return Task.CompletedTask; };
            ch.Value += (_, e) => { if (e.Value.Length > 0) c.SideChanged?.Invoke(e.Value[0]); return Task.CompletedTask; };
            return c;
        }

        public async Task<int?> ReadSide()
        {
            try
            {
                var v = await ch.ReadValueAsync(Timeout);
                return v.Length > 0 ? v[0] : null;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            gone.TrySetResult();
            try { device.DisconnectAsync().Wait(Timeout); } catch { }
        }
    }
}
