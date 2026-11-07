# Timular Time Tracker

A time tracking web app to interface with the Timular ZEI device now that Timular charges a subscription for their product. Built with Claude, so no promises that it's robust. 

Run: `dotnet run` (opens http://localhost:5178). Works on Windows, macOS and Linux. Data lives in:

- Windows: `%LOCALAPPDATA%\TimularTracker\`
- macOS: `~/Library/Application Support/TimularTracker/`
- Linux: `~/.local/share/TimularTracker/`

1. Add projects/tasks and assign each a ZEI side (1-8).
2. Flip the ZEI: the matching task starts, the previous stops. Flipping to an unassigned side stops tracking.
3. Manual start/stop and entry editing work without the device.
4. Export a date range (optionally one client) to .xlsx: Summary, Totals and Entries sheets.

Set `TT_IDLE_MINUTES` to change the idle-disconnect delay (0 disables). Logs go to `tracker.log` in the data folder.

## Building
The target platform comes from the runtime identifier: by default the machine you build on, or pass `-r`.

| Platform | Publish |
|---|---|
| Windows | `dotnet publish -c Release -o dist` |
| Linux | `dotnet publish -r linux-x64 -c Release -o dist` (or `linux-arm64`) |
| macOS | `dotnet publish -r osx-arm64 -c Release -o dist` (or `osx-x64`), then `mac/build.sh dist` |

Each produces a self-contained single file (`TimularTracker[.exe]`), no .NET install needed.

### Windows
Sits in the system tray with no console window. Click the icon to open the page, right-click to quit. Opening the exe again just reopens the page. If the ZEI won't connect, pair it once in Settings > Bluetooth & devices.

### Linux
Runs as a console app (Ctrl+C to quit). Bluetooth goes through BlueZ, so the `bluetooth` service must be running. If the ZEI isn't found, pair it once: `bluetoothctl` then `scan on`, `pair <address>`.

### macOS
Runs as a console app (Ctrl+C to quit). CoreBluetooth isn't reachable from .NET, so a small Swift helper does the Bluetooth part: `mac/build.sh dist` compiles `mac/zei-ble.swift` (needs `xcode-select --install`) and puts `zei-ble` next to the app. On first run macOS asks to allow Bluetooth for your terminal app; allow it, or enable it under System Settings > Privacy & Security > Bluetooth. Set `TT_ZEI_HELPER` to use the helper from another location.
