using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FolderAuditWatchdog;

public static class PdfReportGenerator
{
    public static void Generate(RepositoryInfo repo, AuditDatabaseManager db, string savePath)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        string reportDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var baselineMap = db.GetRepoBaselineMap(repo.Id);
        int baselineCount = baselineMap.Count;
        string baselineDate = baselineMap.Values.Select(v => v.BaselineUpdatedAt).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "Brak danych";

        var snapshots = db.GetSnapshots(repo.Code);
        var lastSnap = snapshots.LastOrDefault();

        int totFiles = lastSnap?.TotalFiles ?? baselineCount;
        int okCount = lastSnap?.OkCount ?? 0;
        int newCount = lastSnap?.NewCount ?? 0;
        int modCount = lastSnap?.ModifiedCount ?? 0;
        int delCount = lastSnap?.DeletedCount ?? 0;
        string sizeStr = lastSnap != null ? $"{lastSnap.TotalSizeMb:F2} MB" : "0.00 MB";

        var auditRows = new List<string[]>();
        string auditCsv = Path.Combine(repo.RootPath, "._audit_meta", "audit_trail.csv");
        if (File.Exists(auditCsv))
        {
            var lines = File.ReadAllLines(auditCsv, Encoding.UTF8).Skip(1);
            foreach (var line in lines)
            {
                var parts = line.Split(';');
                if (parts.Length >= 5)
                    auditRows.Add(parts[..5]);
            }
        }

        string earliest = auditRows.FirstOrDefault()?[0] ?? "Brak danych";
        string latest = auditRows.LastOrDefault()?[0] ?? "Brak danych";

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9.5f).FontColor("#0f172a"));

                page.Content().Column(col =>
                {
                    col.Spacing(10);

                    // Nagłówek
                    col.Item().Text($"Raport audytowy: {repo.Name}").FontSize(18).Bold();
                    col.Item().Text($"Kod repozytorium: {repo.Code}   |   Data wygenerowania: {reportDate}").FontSize(9).FontColor("#475569");

                    // Zakres raportu
                    col.Item().Background("#e2e8f0").Padding(10).Column(box =>
                    {
                        box.Item().Text("Zakres raportu").Bold();
                        box.Item().Text($"Linia bazowa: {baselineDate}   |   Ostatni skan: {repo.LastScanAt ?? "Brak danych"}");
                        box.Item().Text($"Audit trail: {earliest} → {latest}");
                    });

                    // 1. Informacje o repozytorium
                    col.Item().PaddingTop(6).Text("1. Informacje o repozytorium").FontSize(12).Bold();
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); });
                        void AddCell(string lbl, string val)
                        {
                            t.Cell().Border(0.5f).BorderColor("#cbd5e1").Background("#f8fafc").Padding(6).Column(c =>
                            {
                                c.Item().Text(lbl).FontSize(8).Bold().FontColor("#475569");
                                c.Item().Text(val).FontSize(9);
                            });
                        }
                        AddCell("Kod repozytorium", repo.Code);
                        AddCell("Nazwa", repo.Name);
                        AddCell("Właściciel / audytor", string.IsNullOrEmpty(repo.Owner) ? "-" : repo.Owner);
                        AddCell("Ścieżka", repo.RootPath);
                        AddCell("Status", repo.Status);
                        AddCell("Data utworzenia linii bazowej", baselineDate);
                        AddCell("Data ostatniego skanu", repo.LastScanAt ?? "Brak danych");
                        AddCell("Pliki w baseline", baselineCount.ToString());
                    });

                    // 2. Stan z dnia raportu
                    col.Item().PaddingTop(6).Text("2. Stan z dnia raportu").FontSize(12).Bold();
                    col.Item().Row(row =>
                    {
                        row.Spacing(6);
                        void StatBox(string lbl, string val, string bg, string border)
                        {
                            row.RelativeItem().Background(bg).Border(1).BorderColor(border).Padding(8).Column(c =>
                            {
                                c.Item().Text(lbl).FontSize(8).Bold();
                                c.Item().Text(val).FontSize(13).Bold();
                            });
                        }
                        StatBox("Wszystkie", totFiles.ToString(), "#dbeafe", "#1d4ed8");
                        StatBox("Zgodne", okCount.ToString(), "#dcfce7", "#15803d");
                        StatBox("Nowe", newCount.ToString(), "#dbeafe", "#2563eb");
                        StatBox("Zmienione", modCount.ToString(), "#fef3c7", "#d97706");
                        StatBox("Usunięte", delCount.ToString(), "#fee2e2", "#dc2626");
                    });
                    col.Item().Text($"Rozmiar repozytorium: {sizeStr}");

                    // 3. Podsumowanie rozbieżności
                    col.Item().PaddingTop(6).Text("3. Zmiany / rozbieżności wykryte do dnia raportu").FontSize(12).Bold();
                    if (lastSnap != null)
                    {
                        col.Item().Text($"Od momentu utworzenia linii bazowej repozytorium do dnia {reportDate} wykryto: {lastSnap.NewCount} nowych plików, {lastSnap.ModifiedCount} zmienionych plików i {lastSnap.DeletedCount} usuniętych plików. Skan wykonany przez: {lastSnap.Operator}.");
                    }
                    else
                    {
                        col.Item().Text("Brak danych z wykonanych skanów.");
                    }

                    // 4. Rejestr audit trail
                    col.Item().PaddingTop(6).Text("4. Rejestr audit trail od startu linii bazowej do dnia raportu").FontSize(12).Bold();
                    if (auditRows.Count > 0)
                    {
                        int idx = 1;
                        foreach (var r in auditRows)
                        {
                            col.Item().BorderBottom(0.5f).BorderColor("#e2e8f0").PaddingBottom(4).Column(entry =>
                            {
                                entry.Item().Text($"{idx++}. [{r[0]}] {r[1]} | {r[2]}").Bold().FontSize(9);
                                entry.Item().PaddingLeft(10).Text($"Cel: {r[3]}").FontSize(8.5f);
                                entry.Item().PaddingLeft(10).Text($"Opis: {r[4]}").FontSize(8.5f).FontColor("#334155");
                            });
                        }
                    }
                    else
                    {
                        col.Item().Text("Brak wpisów w audit trail dla tego repozytorium.");
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Strona ");
                    x.CurrentPageNumber();
                    x.Span(" z ");
                    x.TotalPages();
                });
            });
        }).GeneratePdf(savePath);
    }
}
