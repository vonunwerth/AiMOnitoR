using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace AIUsageMonitor
{
    public static class TestMain
    {
        static int fails = 0;
        static void Check(bool c, string what) { Console.WriteLine((c ? "OK   " : "FAIL ") + what); if (!c) fails++; }

        static ProviderResult R(string id, string name) { ProviderResult r = new ProviderResult(); r.Id = id; r.Name = name; return r; }

        public static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : ".";
            string fiveReset = DateTime.UtcNow.AddHours(2).AddMinutes(13).ToString("o");
            string weekReset = DateTime.UtcNow.AddDays(3).AddHours(5).ToString("o");

            // ---- Claude
            string claudeJson = "{\"five_hour\":{\"utilization\":28.0,\"resets_at\":\"" + fiveReset + "\"}," +
                "\"seven_day\":{\"utilization\":55,\"resets_at\":\"" + weekReset + "\"}," +
                "\"seven_day_opus\":null,\"seven_day_sonnet\":{\"utilization\":12,\"resets_at\":\"" + weekReset + "\"}," +
                "\"limits\":[null,{\"kind\":\"session\",\"percent\":28},{\"kind\":\"weekly_scoped\",\"group\":\"weekly\",\"percent\":5,\"resets_at\":" + (long)(T.NowUnixSeconds() + 300000) + ",\"scope\":{\"model\":{\"id\":null,\"display_name\":\"Fable\"}}}]," +
                "\"extra_usage\":{\"is_enabled\":false}}";
            ProviderResult c = ClaudeProvider.Map(J.Parse(claudeJson), R("claude", "Claude"));
            c.Plan = ClaudeProvider.PlanName("max", "default_claude_max_5x");
            c.UpdatedAt = DateTime.Now;
            Check(c.BarA != null && Math.Abs(c.BarA.RemainingPct.Value - 72) < 0.01, "Claude 5h = 72% frei");
            Check(c.BarB != null && Math.Abs(c.BarB.RemainingPct.Value - 45) < 0.01, "Claude Woche = 45% frei");
            Check(c.Windows.Count == 4, "Claude 4 Fenster (5h, Woche, Sonnet, Fable) -> " + c.Windows.Count);
            Check(c.Plan == "Max 5x", "Claude Plan 'Max 5x' -> " + c.Plan);
            Check(c.BarA.ResetsAt.HasValue && Math.Abs((c.BarA.ResetsAt.Value - DateTime.Now).TotalMinutes - 133) < 2, "Claude Reset lokal korrekt");

            // ---- ChatGPT / Codex
            double now = T.NowUnixSeconds();
            string codexJson = "{\"plan_type\":\"plus\",\"rate_limit\":{\"allowed\":true,\"limit_reached\":false," +
                "\"primary_window\":{\"used_percent\":91,\"limit_window_seconds\":18000,\"reset_after_seconds\":3000,\"reset_at\":" + (long)(now + 3000) + "}," +
                "\"secondary_window\":{\"used_percent\":40,\"limit_window_seconds\":604800,\"reset_after_seconds\":400000,\"reset_at\":" + (long)(now + 400000) + "}}," +
                "\"credits\":{\"has_credits\":false,\"unlimited\":false,\"balance\":\"0\"}," +
                "\"additional_rate_limits\":[{\"limit_name\":\"GPT-5.3-Codex-Spark\",\"rate_limit\":{\"primary_window\":{\"used_percent\":0,\"limit_window_seconds\":18000,\"reset_at\":" + (long)(now + 18000) + "}}}]}";
            ProviderResult o = ChatGPTProvider.Map(J.Parse(codexJson), R("chatgpt", "ChatGPT"));
            o.Account = "user@example.com"; o.UpdatedAt = DateTime.Now;
            Check(Math.Abs(o.BarA.RemainingPct.Value - 9) < 0.01 && o.BarA.Label == "5 Stunden", "ChatGPT 5h = 9% frei");
            Check(o.BarB.Label == "Woche" && Math.Abs(o.BarB.RemainingPct.Value - 60) < 0.01, "ChatGPT Woche = 60% frei");
            Check(o.Plan == "Plus", "ChatGPT Plan Plus");
            Check(o.Windows.Count == 3, "ChatGPT inkl. Zusatzlimit -> " + o.Windows.Count);

            ProviderResult gm;
            // ---- Antigravity (agy -p /usage --output-format json)
            string agyJson = "{\"conversation_id\":\"\",\"status\":\"SUCCESS\",\"response\":\"Synthetic\",\"command\":{\"name\":\"usage\",\"data\":{\"description\":\"Models share limits\",\"groups\":[" +
                "{\"name\":\"Claude and GPT models\",\"buckets\":[{\"id\":\"3p-weekly\",\"name\":\"Weekly Limit Remaining\",\"window\":\"weekly\",\"remaining_fraction\":0.89,\"reset_time\":\"" + weekReset + "\"},{\"id\":\"3p-5h\",\"window\":\"5h\",\"remaining_fraction\":1.0,\"reset_time\":\"" + fiveReset + "\"}]}," +
                "{\"name\":\"Gemini Models\",\"buckets\":[{\"id\":\"gemini-weekly\",\"window\":\"weekly\",\"remaining_fraction\":0.86,\"reset_time\":\"" + weekReset + "\"},{\"id\":\"gemini-5h\",\"window\":\"5h\",\"remaining_fraction\":0.95,\"reset_time\":\"" + fiveReset + "\"}]}]}}}";
            ProviderResult ag = Antigravity.Map(Antigravity.ExtractJson("noise line\n" + agyJson), R("gemini", "Gemini"));
            Check(ag.BarA.Label == "Gemini · 5 Stunden" && Math.Abs(ag.BarA.RemainingPct.Value - 95) < 0.01, "Antigravity Gemini 5h 95% -> " + ag.BarA.Label);
            Check(ag.BarB.Label == "Gemini · Woche" && Math.Abs(ag.BarB.RemainingPct.Value - 86) < 0.01, "Antigravity Gemini Woche 86%");
            Check(ag.Windows.Count == 4 && ag.Windows[0] == ag.BarA && ag.Windows[2].Label == "Claude & GPT · 5 Stunden", "Antigravity Reihenfolge");
            string starter = "{\"status\":\"SUCCESS\",\"command\":{\"name\":\"usage\",\"data\":{\"groups\":[{\"name\":\"Gemini Models\",\"buckets\":[{\"id\":\"gemini-weekly\",\"window\":\"weekly\",\"remaining_fraction\":0.5}]}]}}}";
            ProviderResult st = Antigravity.Map(J.Parse(starter), R("gemini", "Gemini"));
            Check(st.BarA != null && st.BarA.Label == "Gemini · Woche" && st.BarB == null, "Antigravity Starter nur Woche");
            gm = ag; gm.UpdatedAt = DateTime.Now;

            // ---- Copilot
            string copJson = "{\"login\":\"octocat\",\"copilot_plan\":\"individual_pro\",\"access_type_sku\":\"monthly_subscriber\",\"quota_reset_date\":\"2026-12-01\"," +
                "\"quota_snapshots\":{\"chat\":{\"entitlement\":0,\"remaining\":0,\"percent_remaining\":100,\"unlimited\":true,\"quota_id\":\"chat\"}," +
                "\"completions\":{\"entitlement\":0,\"remaining\":0,\"percent_remaining\":100,\"unlimited\":true}," +
                "\"premium_interactions\":{\"entitlement\":300,\"remaining\":112,\"percent_remaining\":37.3,\"unlimited\":false,\"overage_count\":0,\"overage_permitted\":false}}}";
            ProviderResult cp = CopilotProvider.Map(J.Parse(copJson), R("copilot", "Copilot"));
            cp.UpdatedAt = DateTime.Now;
            Check(Math.Abs(cp.BarA.RemainingPct.Value - 37.3) < 0.01, "Copilot Premium 37,3%");
            Check(cp.BarB == null, "Copilot: kein zweiter Balken bei unbegrenztem Chat");
            Check(cp.BarA.ResetsAt.HasValue && cp.BarA.ResetsAt.Value.ToUniversalTime().Day == 1, "Copilot Reset 1. des Monats");
            Check(cp.BarA.Note == "112 von 300 übrig", "Copilot Note -> " + cp.BarA.Note);

            string freeJson = "{\"copilot_plan\":\"free\",\"limited_user_quotas\":{\"chat\":12,\"completions\":1500},\"monthly_quotas\":{\"chat\":50,\"completions\":2000},\"limited_user_reset_date\":\"2026-12-05\"}";
            ProviderResult cf = CopilotProvider.Map(J.Parse(freeJson), R("copilot", "Copilot"));
            Check(cf.BarA != null && Math.Abs(cf.BarA.RemainingPct.Value - 24) < 0.01 && cf.BarB != null, "Copilot Free: Chat 24%, Completions zweiter Balken");

            // Fehlerfälle
            ProviderResult err = R("gemini", "Gemini"); err.NotConfigured = true;
            err.Error = "Gemini läuft für Privatkonten über Antigravity. Antigravity CLI installieren, 'agy' starten und mit Google anmelden.";
            ProviderResult stale = ChatGPTProvider.Map(J.Parse(codexJson), R("chatgpt", "ChatGPT"));
            stale.Error = "Netzwerkfehler: Der Remotename konnte nicht aufgelöst werden"; stale.Stale = true; stale.UpdatedAt = DateTime.Now.AddMinutes(-12);
            ProviderResult copNc = R("copilot", "Copilot"); copNc.NotConfigured = true;
            copNc.Error = "Nicht mit GitHub verbunden. Rechtsklick auf ein Symbol → 'GitHub Copilot anmelden…'.";

            // Tooltips
            Console.WriteLine("Tooltip: " + IconRenderer.Tooltip(c, "Claude"));
            Console.WriteLine("Tooltip: " + IconRenderer.Tooltip(o, "ChatGPT"));
            Console.WriteLine("Tooltip: " + IconRenderer.Tooltip(gm, "Gemini"));
            Console.WriteLine("Tooltip: " + IconRenderer.Tooltip(cp, "Copilot"));
            Console.WriteLine("Reset-Text: " + Fmt.Reset(c.BarA) + " | " + Fmt.Reset(c.BarB) + " | " + Fmt.Reset(cp.BarA));
            Check(IconRenderer.Tooltip(c, "Claude").Length <= 63, "Tooltip <= 63 Zeichen");

            // Config Round-Trip
            Config cfg = new Config();
            Dictionary<string, Color> brands = new Dictionary<string, Color>();
            Provider[] provs = { new ClaudeProvider(), new ChatGPTProvider(), new GeminiProvider(), new CopilotProvider() };
            foreach (Provider p in provs) brands[p.Id] = p.Brand;

            // ---- Icons rendern (Taskleisten-Vorschau)
            ProviderResult[] iconSet = { c, o, gm, cp };
            foreach (int size in new int[] { 16, 24, 32 })
            {
                foreach (bool dark in new bool[] { true, false })
                {
                    int zoom = 8;
                    int n = iconSet.Length + 2;
                    using (Bitmap strip = new Bitmap(n * (size + 8) * zoom, (size + 8) * zoom))
                    using (Graphics g = Graphics.FromImage(strip))
                    {
                        g.Clear(dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(238, 238, 238));
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        ProviderResult[] all = { c, o, gm, cp, err, stale };
                        for (int i = 0; i < all.Length; i++)
                        {
                            using (Bitmap ic = IconRenderer.Render(all[i], brands[all[i].Id], size, dark, cfg))
                                g.DrawImage(ic, new Rectangle((i * (size + 8) + 4) * zoom, 4 * zoom, size * zoom, size * zoom));
                        }
                        strip.Save(Path.Combine(outDir, "icons_" + size + (dark ? "_dark" : "_light") + ".png"), ImageFormat.Png);
                    }
                }
            }

            // ---- Farbskala-Vorschau: Paare (5h, Woche) je Anbieter
            double[][] pairs = { new double[] { 100, 80 }, new double[] { 70, 55 }, new double[] { 45, 30 }, new double[] { 20, 12 }, new double[] { 8, 2 }, new double[] { 0, 0.3 } };
            string[] ids = { "claude", "chatgpt", "gemini", "copilot", "claude", "chatgpt" };
            foreach (bool dark in new bool[] { true, false })
            foreach (int size in new int[] { 16, 24 })
            {
                int zoom = 8;
                using (Bitmap strip = new Bitmap(pairs.Length * (size + 8) * zoom, (size + 8) * zoom))
                using (Graphics g = Graphics.FromImage(strip))
                {
                    g.Clear(dark ? Color.FromArgb(28, 28, 28) : Color.FromArgb(238, 238, 238));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    for (int i = 0; i < pairs.Length; i++)
                    {
                        ProviderResult pr0 = R(ids[i], ids[i]);
                        pr0.BarA = new UsageWindow("a", "a"); pr0.BarA.RemainingPct = pairs[i][0];
                        pr0.BarB = new UsageWindow("b", "b"); pr0.BarB.RemainingPct = pairs[i][1];
                        using (Bitmap ic = IconRenderer.Render(pr0, brands[ids[i]], size, dark, cfg))
                            g.DrawImage(ic, new Rectangle((i * (size + 8) + 4) * zoom, 4 * zoom, size * zoom, size * zoom));
                    }
                    strip.Save(Path.Combine(outDir, "scale_" + size + (dark ? "_dark" : "_light") + ".png"), ImageFormat.Png);
                }
            }

            // ---- Popup rendern
            foreach (bool dark in new bool[] { true, false, true })
            {
                bool compact = dark && File.Exists(Path.Combine(outDir, "popup_dark.png"));
                PopupRenderer pr = new PopupRenderer();
                pr.Cfg = cfg; pr.Th = Theme.Make(dark); pr.Brands = brands; pr.Scale = 1.25f; pr.Compact = compact;
                pr.Data = new List<ProviderResult> { c, stale, gm, cp };
                if (!dark) pr.Data = new List<ProviderResult> { c, o, err, copNc };
                int h;
                using (Bitmap tmp = new Bitmap(10, 10)) using (Graphics g0 = Graphics.FromImage(tmp)) h = pr.Render(g0, false);
                using (Bitmap bmp = new Bitmap(pr.Width, h))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    pr.Render(g, true);
                    bmp.Save(Path.Combine(outDir, "popup_" + (compact ? "compact" : dark ? "dark" : "light") + ".png"), ImageFormat.Png);
                }
                Console.WriteLine("Popup " + (dark ? "dark" : "light") + ": " + pr.Width + "x" + h + ", Hits=" + pr.Hits.Count);
            }

            // ---- Doku-Bilder (neutrale Beispieldaten)
            {
                PopupRenderer pr = new PopupRenderer();
                pr.Cfg = cfg; pr.Th = Theme.Make(true); pr.Brands = brands; pr.Scale = 1.5f;
                pr.Data = new List<ProviderResult> { c, o, gm, cp };
                int h;
                using (Bitmap tmp = new Bitmap(10, 10)) using (Graphics g0 = Graphics.FromImage(tmp)) h = pr.Render(g0, false);
                using (Bitmap bmp = new Bitmap(pr.Width, h))
                using (Graphics g = Graphics.FromImage(bmp)) { pr.Render(g, true); bmp.Save(Path.Combine(outDir, "docs_popup.png"), ImageFormat.Png); }

                int size = 24, zoom = 4, w = 4 * (size + 6) + 8;
                using (Bitmap bar = new Bitmap(w * zoom, (size + 12) * zoom))
                using (Graphics g = Graphics.FromImage(bar))
                {
                    g.Clear(Color.FromArgb(32, 32, 32));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    ProviderResult[] four = { c, o, gm, cp };
                    for (int i = 0; i < 4; i++)
                        using (Bitmap ic = IconRenderer.Render(four[i], brands[four[i].Id], size, true, cfg))
                            g.DrawImage(ic, new Rectangle((4 + i * (size + 6)) * zoom, 6 * zoom, size * zoom, size * zoom));
                    bar.Save(Path.Combine(outDir, "docs_tray.png"), ImageFormat.Png);
                }
            }

            Console.WriteLine(fails == 0 ? "ALLE TESTS OK" : fails + " FEHLER");
            return fails;
        }
    }
}
