# Timular Time Tracker

Run: `dotnet run` (opens http://localhost:5178). Data lives in `%LOCALAPPDATA%\TimularTracker\tracker.db`.

1. Add projects/tasks and assign each a ZEI side (1-8).
2. Flip the ZEI: the matching task starts, the previous stops. Flipping to an unassigned side stops tracking.
3. Manual start/stop and entry editing work without the device.
4. Export a date range (optionally one client) to .xlsx: Summary, Totals and Entries sheets.

If the ZEI won't connect, pair it once in Windows Settings > Bluetooth & devices.

## Standalone exe
`dotnet publish -c Release -o dist` builds `dist\TimularTracker.exe` (self-contained, no .NET install needed). Double-click to run; it sits in the system tray with no console window. Click the tray icon to open the page, or right-click it to quit. Opening the exe again just reopens the page. Logs go to `%LOCALAPPDATA%\TimularTracker\tracker.log`. Set `TT_IDLE_MINUTES` to change the idle-disconnect delay (0 disables).
