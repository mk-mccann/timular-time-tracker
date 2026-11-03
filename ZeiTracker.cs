namespace TimeTracker;

/// <summary>Connects to a Timeular ZEI over BLE and reports which side is facing up (1-8, 0 = none).</summary>
public class ZeiTracker(Db db, ILogger log) : BackgroundService
{
    readonly IZeiLink link = ZeiLinks.Create();

    public string Status { get; private set; } = "Searching for ZEI…";
    public bool Connected { get; private set; }
    public int Side { get; private set; }
    public int RawSide { get; private set; } = -1; // last value read, even if ignored

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var idleDevice = await RunOnce(ct);
                if (idleDevice != null)
                {
                    Connected = false;
                    await WaitForWake(idleDevice, ct);
                    continue; // reconnect straight away
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { Status = "Error: " + e.Message; log.LogWarning("ZEI: {Message}", e.Message); }
            Connected = false;
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
        }
    }

    // Disconnect after this long resting at side 0 (override with TT_IDLE_MINUTES; 0 disables).
    static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(
        double.TryParse(Environment.GetEnvironmentVariable("TT_IDLE_MINUTES"), out var m) ? m : 5);

    /// <summary>Runs one connection. Returns the device if we hung up because it sat idle, else null.</summary>
    async Task<object?> RunOnce(CancellationToken ct)
    {
        Status = "Searching for ZEI…";
        var device = await link.FindDevice(ct);
        Status = "Connecting…";

        using var conn = await link.Connect(device, ct);
        conn.SideChanged += OnValue;

        if (await conn.ReadSide() is { } first) OnValue(first);

        Connected = true;
        Status = "Connected";
        // Poll as a backup to notifications (which can be dropped silently).
        DateTime? idleSince = null;
        var idle = false;
        while (!conn.Disconnected.IsCompleted)
        {
            await Task.WhenAny(conn.Disconnected, Task.Delay(1000, ct));
            ct.ThrowIfCancellationRequested();
            if (conn.Disconnected.IsCompleted) break;
            if (await conn.ReadSide() is { } r) OnValue(r);

            if (RawSide != 0) idleSince = null;
            else if ((idleSince ??= DateTime.UtcNow) is var t && IdleAfter > TimeSpan.Zero && DateTime.UtcNow - t >= IdleAfter)
            { idle = true; break; }
        }
        GC.KeepAlive(conn); // some backends rely on it staying alive for notifications to keep firing
        Status = "Disconnected";
        return idle ? device : null;
    }

    /// <summary>Connection is released; wait (passive listen) until the ZEI advertises again, i.e. it was picked up.</summary>
    async Task WaitForWake(object device, CancellationToken ct)
    {
        Status = "Idle – disconnected, waiting for ZEI to wake";
        RawSide = -1;
        // The ZEI may advertise briefly right after we hang up, so ignore adverts for a grace period.
        await Task.Delay(TimeSpan.FromSeconds(60), ct);
        await link.WaitForWake(device, ct);
    }

    void OnValue(int side)
    {
        RawSide = side;
        if (side == Side || side > 8) return; // 9-13 are fixed, non-assignable sides: ignore rather than stop
        Side = side;
        db.ApplySide(side);
    }
}
