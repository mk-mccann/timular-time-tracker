namespace TimeTracker;

/// <summary>System tray icon: click to open the page, right-click for Open/Quit.</summary>
public static class Tray
{
    /// <summary>Starts the icon on its own UI thread. The thread ends once the app is stopping.</summary>
    public static Thread Start(IHostApplicationLifetime lifetime, Action open)
    {
        var thread = new Thread(() =>
        {
            using var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => open());
            menu.Items.Add("Quit", null, (_, _) => lifetime.StopApplication());

            using var appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            using var icon = new NotifyIcon { Icon = appIcon, Text = "Timular Tracker", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };

            lifetime.ApplicationStopping.Register(Application.Exit);
            Application.Run();
            icon.Visible = false;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }
}
