using ClosedXML.Excel;

namespace TimeTracker;

public static class Exporter
{
    public static byte[] ToXlsx(List<Segment> segs, DateOnly from, DateOnly to)
    {
        var tz = TimeZoneInfo.Local;
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, tz);

        using var wb = new XLWorkbook();

        // Summary: one row per day / client / activity, hours as decimals so it can be summed and invoiced.
        var ws = wb.AddWorksheet("Summary");
        ws.Cell(1, 1).Value = $"Time report {from:yyyy-MM-dd} to {to:yyyy-MM-dd}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        string[] h1 = ["Date", "Client", "Project / Task", "Hours"];
        for (int i = 0; i < h1.Length; i++) ws.Cell(3, i + 1).Value = h1[i];
        ws.Range(3, 1, 3, 4).Style.Font.Bold = true;

        var groups = segs.GroupBy(s => (s.Day, s.Client, s.Name)).OrderBy(g => g.Key.Day).ThenBy(g => g.Key.Client).ThenBy(g => g.Key.Name);
        int r = 4;
        foreach (var g in groups)
        {
            ws.Cell(r, 1).Value = g.Key.Day.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 2).Value = g.Key.Client;
            ws.Cell(r, 3).Value = g.Key.Name;
            ws.Cell(r, 4).Value = Math.Round(g.Sum(s => s.Seconds) / 3600, 2);
            r++;
        }
        ws.Cell(r + 1, 3).Value = "Total hours";
        ws.Cell(r + 1, 3).Style.Font.Bold = true;
        ws.Cell(r + 1, 4).FormulaA1 = r > 4 ? $"SUM(D4:D{r - 1})" : "0";
        ws.Cell(r + 1, 4).Style.Font.Bold = true;
        ws.Column(4).Style.NumberFormat.Format = "0.00";
        ws.Columns().AdjustToContents();

        // Totals by client/activity across the range
        var ws2 = wb.AddWorksheet("Totals");
        string[] h2 = ["Client", "Project / Task", "Hours"];
        for (int i = 0; i < h2.Length; i++) ws2.Cell(1, i + 1).Value = h2[i];
        ws2.Range(1, 1, 1, 3).Style.Font.Bold = true;
        r = 2;
        foreach (var g in segs.GroupBy(s => (s.Client, s.Name)).OrderBy(g => g.Key.Client).ThenBy(g => g.Key.Name))
        {
            ws2.Cell(r, 1).Value = g.Key.Client;
            ws2.Cell(r, 2).Value = g.Key.Name;
            ws2.Cell(r, 3).Value = Math.Round(g.Sum(s => s.Seconds) / 3600, 2);
            r++;
        }
        ws2.Column(3).Style.NumberFormat.Format = "0.00";
        ws2.Columns().AdjustToContents();

        // Detail: every entry
        var ws3 = wb.AddWorksheet("Entries");
        string[] h3 = ["Date", "Client", "Project / Task", "Start", "End", "Duration (h:mm)", "Hours", "Note"];
        for (int i = 0; i < h3.Length; i++) ws3.Cell(1, i + 1).Value = h3[i];
        ws3.Range(1, 1, 1, h3.Length).Style.Font.Bold = true;
        r = 2;
        foreach (var s in segs.OrderBy(s => s.StartUtc))
        {
            ws3.Cell(r, 1).Value = s.Day.ToDateTime(TimeOnly.MinValue);
            ws3.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws3.Cell(r, 2).Value = s.Client;
            ws3.Cell(r, 3).Value = s.Name;
            ws3.Cell(r, 4).Value = Local(s.StartUtc).TimeOfDay;
            ws3.Cell(r, 5).Value = Local(s.EndUtc).TimeOfDay;
            ws3.Cell(r, 4).Style.DateFormat.Format = ws3.Cell(r, 5).Style.DateFormat.Format = "hh:mm";
            ws3.Cell(r, 6).Value = TimeSpan.FromSeconds(Math.Round(s.Seconds));
            ws3.Cell(r, 6).Style.DateFormat.Format = "[h]:mm:ss";
            ws3.Cell(r, 7).Value = Math.Round(s.Seconds / 3600, 2);
            ws3.Cell(r, 8).Value = s.Note;
            r++;
        }
        ws3.Column(7).Style.NumberFormat.Format = "0.00";
        ws3.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
