using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace TimeTracker;

/// <summary>Windows backend: WinRT Bluetooth LE. The device handle is the Bluetooth address (ulong).</summary>
public class WindowsZeiLink : IZeiLink
{
    public async Task<object> FindDevice(CancellationToken ct)
    {
        // Prefer an already-paired ZEI: no radio scanning needed, so other Bluetooth devices are left alone.
        var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var info in paired.Where(i => ZeiLinks.IsZeiName(i.Name)))
        {
            using var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (dev != null) return dev.BluetoothAddress;
        }

        // Otherwise a short, passive scan (no scan requests sent), then give up and back off.
        var found = new TaskCompletionSource<ulong>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.Received += (_, a) =>
        {
            if (ZeiLinks.IsZeiName(a.Advertisement.LocalName) || a.Advertisement.ServiceUuids.Contains(ZeiLinks.ServiceUuid))
                found.TrySetResult(a.BluetoothAddress);
        };
        watcher.Start();
        try
        {
            using var reg = ct.Register(() => found.TrySetCanceled());
            var done = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
            if (done != found.Task) throw new Exception("ZEI not found nearby");
            return await found.Task;
        }
        finally { watcher.Stop(); }
    }

    public async Task<IZeiConnection> Connect(object device, CancellationToken ct)
    {
        var address = (ulong)device;
        var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                  ?? throw new Exception("Could not open device");
        try { return await Conn.Open(dev); }
        catch { dev.Dispose(); throw; }
    }

    public async Task WaitForWake(object device, CancellationToken ct)
    {
        var address = (ulong)device;
        var woke = new TaskCompletionSource();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.Received += (_, a) => { if (a.BluetoothAddress == address) woke.TrySetResult(); };
        watcher.Start();
        try
        {
            using var reg = ct.Register(() => woke.TrySetCanceled());
            await woke.Task;
        }
        finally { watcher.Stop(); }
    }

    sealed class Conn : IZeiConnection
    {
        readonly BluetoothLEDevice device;
        readonly GattSession session;
        // Held so the WinRT objects stay alive and notifications keep firing.
        GattDeviceServicesResult? svc;
        GattCharacteristicsResult? chars;
        GattCharacteristic ch = null!;
        readonly TaskCompletionSource gone = new();

        public Task Disconnected => gone.Task;
        public event Action<int>? SideChanged;

        Conn(BluetoothLEDevice device, GattSession session) { this.device = device; this.session = session; }

        public static async Task<Conn> Open(BluetoothLEDevice device)
        {
            var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
            session.MaintainConnection = true;
            var c = new Conn(device, session);
            try
            {
                c.svc = await device.GetGattServicesForUuidAsync(ZeiLinks.ServiceUuid, BluetoothCacheMode.Uncached);
                if (c.svc.Status != GattCommunicationStatus.Success || c.svc.Services.Count == 0)
                    throw new Exception($"ZEI service not found ({c.svc.Status}). Try pairing it in Windows Bluetooth settings.");

                c.chars = await c.svc.Services[0].GetCharacteristicsForUuidAsync(ZeiLinks.OrientationUuid, BluetoothCacheMode.Uncached);
                if (c.chars.Status != GattCommunicationStatus.Success || c.chars.Characteristics.Count == 0)
                    throw new Exception("Orientation characteristic not found");
                c.ch = c.chars.Characteristics[0];

                device.ConnectionStatusChanged += (d, _) => { if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected) c.gone.TrySetResult(); };
                c.ch.ValueChanged += (_, args) => c.SideChanged?.Invoke(Read(args.CharacteristicValue));

                var sub = await c.ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (sub != GattCommunicationStatus.Success) throw new Exception("Could not subscribe: " + sub);
                return c;
            }
            catch { c.Dispose(); throw; }
        }

        public async Task<int?> ReadSide()
        {
            var r = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
            return r.Status == GattCommunicationStatus.Success ? Read(r.Value) : null;
        }

        static int Read(IBuffer buf) => DataReader.FromBuffer(buf).ReadByte();

        public void Dispose()
        {
            session.Dispose();
            device.Dispose();
            gone.TrySetResult();
        }
    }
}
