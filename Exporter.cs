using ClosedXML.Excel;

namespace TimeTracker;

public static class Exporter
{
    const string Money = "#,##0.00";

    public static byte[] ToXlsx(List<Segment> segs, DateOnly from, DateOnly to)
    {
        var tz = TimeZoneInfo.Local;
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        static double H(IEnumerable<Segment> s) => Math.Round(s.Sum(x => x.Seconds) / 3600, 2);

        using var wb = new XLWorkbook();
        var clients = segs.GroupBy(s => s.Client).OrderBy(g => g.Key == "" ? 1 : 0).ThenBy(g => g.Key).ToList();

        // Overview: one row per client.
        var ov = wb.AddWorksheet("Overview");
        ov.Cell(1, 1).Value = $"Time report {from:yyyy-MM-dd} to {to:yyyy-MM-dd}";
        ov.Cell(1, 1).Style.Font.Bold = true;
        Header(ov, 3, "Client", "Hours", "Billable hours", "Non-billable hours", "Amount");
        int r = 4;
        foreach (var g in clients)
        {
            ov.Cell(r, 1).Value = g.Key == "" ? "(no client)" : g.Key;
            ov.Cell(r, 2).Value = H(g);
            ov.Cell(r, 3).Value = H(g.Where(s => s.Billable));
            ov.Cell(r, 4).Value = H(g.Where(s => !s.Billable));
            ov.Cell(r, 5).Value = Math.Round(g.Sum(s => s.Amount), 2);
            r++;
        }
        ov.Cell(r, 1).Value = "Total";
        for (int c = 2; c <= 5; c++)
            ov.Cell(r, c).FormulaA1 = r > 4 ? $"SUM({Col(c)}4:{Col(c)}{r - 1})" : "0";
        ov.Row(r).Style.Font.Bold = true;
        ov.Range(4, 2, r, 4).Style.NumberFormat.Format = "0.00";
        ov.Range(4, 5, r, 5).Style.NumberFormat.Format = Money;
        ov.Columns().AdjustToContents();

        // One sheet per client: totals by task, then every entry.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Overview" };
        foreach (var g in clients)
        {
            var ws = wb.AddWorksheet(SheetName(g.Key == "" ? "No client" : g.Key, used));
            ws.Cell(1, 1).Value = $"{(g.Key == "" ? "(no client)" : g.Key)}: {from:yyyy-MM-dd} to {to:yyyy-MM-dd}";
            ws.Cell(1, 1).Style.Font.Bold = true;

            Header(ws, 3, "Project / Task", "Billable", "Hours", "Amount");
            r = 4;
            foreach (var t in g.GroupBy(s => (s.Name, s.Billable)).OrderBy(t => t.Key.Name))
            {
                ws.Cell(r, 1).Value = t.Key.Name;
                ws.Cell(r, 2).Value = t.Key.Billable ? "Yes" : "No";
                ws.Cell(r, 3).Value = H(t);
                ws.Cell(r, 4).Value = Math.Round(t.Sum(s => s.Amount), 2);
                r++;
            }
            ws.Cell(r, 1).Value = "Total";
            ws.Cell(r, 3).FormulaA1 = r > 4 ? $"SUM(C4:C{r - 1})" : "0";
            ws.Cell(r, 4).FormulaA1 = r > 4 ? $"SUM(D4:D{r - 1})" : "0";
            ws.Row(r).Style.Font.Bold = true;
            ws.Range(4, 3, r, 3).Style.NumberFormat.Format = "0.00";
            ws.Range(4, 4, r, 4).Style.NumberFormat.Format = Money;

            r += 2;
            Header(ws, r, "Date", "Project / Task", "Start", "End", "Duration (h:mm)", "Hours", "Billable", "Rate", "Amount", "Note");
            r++;
            foreach (var s in g.OrderBy(s => s.StartUtc))
            {
                ws.Cell(r, 1).Value = s.Day.ToDateTime(TimeOnly.MinValue);
                ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
                ws.Cell(r, 2).Value = s.Name;
                ws.Cell(r, 3).Value = Local(s.StartUtc).TimeOfDay;
                ws.Cell(r, 4).Value = Local(s.EndUtc).TimeOfDay;
                ws.Cell(r, 3).Style.DateFormat.Format = ws.Cell(r, 4).Style.DateFormat.Format = "hh:mm";
                ws.Cell(r, 5).Value = TimeSpan.FromSeconds(Math.Round(s.Seconds));
                ws.Cell(r, 5).Style.DateFormat.Format = "[h]:mm:ss";
                ws.Cell(r, 6).Value = Math.Round(s.Seconds / 3600, 2);
                ws.Cell(r, 6).Style.NumberFormat.Format = "0.00";
                ws.Cell(r, 7).Value = s.Billable ? "Yes" : "No";
                ws.Cell(r, 8).Value = s.Rate;
                ws.Cell(r, 9).Value = Math.Round(s.Amount, 2);
                ws.Range(r, 8, r, 9).Style.NumberFormat.Format = Money;
                ws.Cell(r, 10).Value = s.Note;
                r++;
            }
            ws.Columns().AdjustToContents();
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    static void Header(IXLWorksheet ws, int row, params string[] cols)
    {
        for (int i = 0; i < cols.Length; i++) ws.Cell(row, i + 1).Value = cols[i];
        ws.Range(row, 1, row, cols.Length).Style.Font.Bold = true;
    }

    static string Col(int c) => ((char)('A' + c - 1)).ToString();

    // Excel sheet names: max 31 chars, no []:*?/\ and unique (case-insensitive).
    static string SheetName(string name, HashSet<string> used)
    {
        var clean = new string(name.Select(ch => "[]:*?/\\".Contains(ch) ? '_' : ch).ToArray()).Trim('\'').Trim();
        if (clean == "") clean = "Client";
        if (clean.Length > 31) clean = clean[..31];
        var n = clean; int i = 2;
        while (!used.Add(n)) { var suffix = $" ({i++})"; n = clean[..Math.Min(clean.Length, 31 - suffix.Length)] + suffix; }
        return n;
    }
}
