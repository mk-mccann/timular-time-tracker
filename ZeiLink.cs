namespace TimeTracker;

/// <summary>Platform Bluetooth backend for the ZEI. One implementation per OS (ZeiLinkWindows/Linux/Mac.cs).</summary>
public interface IZeiLink
{
    /// <summary>Locates the ZEI nearby and returns an opaque handle for it. Throws if it can't be found.</summary>
    Task<object> FindDevice(CancellationToken ct);

    /// <summary>Connects and starts reporting orientation. Throws if the ZEI can't be used.</summary>
    Task<IZeiConnection> Connect(object device, CancellationToken ct);

    /// <summary>With no connection held, completes once the ZEI advertises again (it was picked up).</summary>
    Task WaitForWake(object device, CancellationToken ct);
}

public interface IZeiConnection : IDisposable
{
    /// <summary>Completes when the link drops. Never faults.</summary>
    Task Disconnected { get; }

    /// <summary>Raised from a notification: the raw orientation byte (0 = none, 1-8 = sides, 9+ = fixed sides).</summary>
    event Action<int> SideChanged;

    /// <summary>Reads the orientation now (backup for dropped notifications). Null if the read failed.</summary>
    Task<int?> ReadSide();
}

public static class ZeiLinks
{
    public static readonly Guid ServiceUuid = Guid.Parse("c7e70010-c847-11e6-8175-8c89a55d403c");
    public static readonly Guid OrientationUuid = Guid.Parse("c7e70012-c847-11e6-8175-8c89a55d403c");

    public static bool IsZeiName(string? name) =>
        name != null && (name.Contains("Timeular", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("ZEI", StringComparison.OrdinalIgnoreCase));

    public static IZeiLink Create() =>
#if WINDOWS
        new WindowsZeiLink();
#elif LINUX
        new LinuxZeiLink();
#elif MACOS
        new MacZeiLink();
#else
        throw new PlatformNotSupportedException();
#endif
}
