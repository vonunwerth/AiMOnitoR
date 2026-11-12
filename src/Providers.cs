// AI Usage Monitor - Anbieter (Claude, ChatGPT/Codex, Gemini, GitHub Copilot)
//
// Alle Abfragen nutzen die Logins, die die jeweiligen offiziellen CLIs bereits lokal gespeichert haben:
//   Claude   -> %USERPROFILE%\.claude\.credentials.json   (Claude Code)
//   ChatGPT  -> %USERPROFILE%\.codex\auth.json            (OpenAI Codex CLI, Login mit ChatGPT)
//   Gemini   -> Antigravity CLI "agy" (verwaltet den Google-Login selbst)
//   Copilot  -> eigener GitHub-Login (Device-Flow), github-copilot\apps.json oder "gh auth token"
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AIUsageMonitor
{
    public abstract class Provider
    {
        public string Id;
        public string Name;
        public System.Drawing.Color Brand;
        public Config Cfg;
        public int MinRefreshMinutes = 1;   // Mindestabstand automatischer Abrufe

        public abstract ProviderResult FetchCore();

        public ProviderResult Fetch()
        {
            ProviderResult r;
            try
            {
                r = FetchCore();
            }
            catch (ProviderException ex)
            {
                r = NewResult();
                r.Error = ex.Message;
                r.NotConfigured = ex.NotConfigured;
                r.RateLimited = ex.RateLimited;
                r.PauseMinutes = ex.PauseMinutes;
            }
            catch (Exception ex)
            {
                r = NewResult();
                r.Error = "Unerwarteter Fehler: " + ex.Message;
                Log.Write(Name + ": " + ex);
            }
            r.UpdatedAt = DateTime.Now;
            if (r.Error != null) Log.Write(Name + ": " + r.Error);
            return r;
        }

        // HTTP-Fehler -> Exception; 429 wird als Rate-Limit markiert (App pausiert den Anbieter dann kurz)
        protected ProviderException Fail(HttpResult r)
        {
            if (r.Status == 429)
            {
                ProviderException e = new ProviderException(Name + " meldet zu viele Abfragen (429) – pausiere einige Minuten.");
                e.RateLimited = true;
                return e;
            }
            return new ProviderException(Http.Describe(r));
        }

        public ProviderResult NewResult()
        {
            ProviderResult r = new ProviderResult();
            r.Id = Id;
            r.Name = Name;
            return r;
        }

        protected static UsageWindow FromUsedPct(string label, string shortLabel, double? usedPct, DateTime? reset)
        {
            UsageWindow w = new UsageWindow(label, shortLabel);
            if (usedPct.HasValue) w.RemainingPct = Math.Max(0, Math.Min(100, 100 - usedPct.Value));
            w.ResetsAt = reset;
            return w;
        }
    }

    // ================================================================== Claude
    public class ClaudeProvider : Provider
    {
        const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

        public ClaudeProvider()
        {
            Id = "claude"; Name = "Claude";
            Brand = System.Drawing.Color.FromArgb(217, 119, 87);
        }

        static string CredPath()
        {
            string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Files.Home, ".claude");
            return Path.Combine(dir, ".credentials.json");
        }

        class Creds
        {
            public string Access, Refresh, SubscriptionType, Tier;
            public DateTime? Expires;
            public bool Expired { get { return Expires.HasValue && Expires.Value <= DateTime.Now.AddSeconds(60); } }
        }

        static Creds ReadCreds()
        {
            string p = CredPath();
            if (!File.Exists(p))
                throw new ProviderException("Kein Claude-Code-Login gefunden. In einem Terminal 'claude' starten und mit /login (Claude-Abo) anmelden.", true);
            Dictionary<string, object> j = J.Parse(Files.ReadText(p));
            Dictionary<string, object> o = J.Obj(j, "claudeAiOauth");
            if (o == null || string.IsNullOrEmpty(J.Str(o, "accessToken")))
                throw new ProviderException("Claude Code ist nicht mit einem Claude-Abo angemeldet (nur API-Key/MCP). In 'claude' mit /login anmelden.", true);
            Creds c = new Creds();
            c.Access = J.Str(o, "accessToken");
            c.Refresh = J.Str(o, "refreshToken");
            c.SubscriptionType = J.Str(o, "subscriptionType");
            c.Tier = J.Str(o, "rateLimitTier");
            c.Expires = T.Flexible(J.Get(o, "expiresAt"));
            return c;
        }

        Creds RefreshCreds(Creds c)
        {
            if (!Cfg.ClaudeAllowTokenRefresh || string.IsNullOrEmpty(c.Refresh))
                throw new ProviderException("Claude-Token abgelaufen. Einmal 'claude' im Terminal starten, dann wird er erneuert.");
            // Erst prüfen, ob Claude Code den Token inzwischen selbst erneuert hat
            Creds fresh = ReadCreds();
            if (fresh.Access != c.Access && !fresh.Expired) return fresh;

            HttpResult r = Http.Send("POST", TokenUrl, null,
                Http.Form("grant_type", "refresh_token", "refresh_token", fresh.Refresh, "client_id", ClientId),
                "application/x-www-form-urlencoded");
            if (!r.Ok)
            {
                Log.Write("Claude Token-Refresh fehlgeschlagen: HTTP " + r.Status + (r.NetworkError ?? ""));
                throw new ProviderException("Claude-Token konnte nicht erneuert werden (" +
                    (r.NetworkError ?? "HTTP " + r.Status) + "). 'claude' starten bzw. /login.");
            }
            Dictionary<string, object> t = J.Parse(r.Body);
            string access = J.Str(t, "access_token");
            if (string.IsNullOrEmpty(access)) throw new ProviderException("Claude-Token-Erneuerung: ungültige Antwort.");
            string refresh = J.Str(t, "refresh_token") ?? fresh.Refresh;
            double expiresIn = J.Num(t, "expires_in") ?? 3600;
            long expiresMs = (long)((T.NowUnixSeconds() + expiresIn) * 1000);

            // Zurückschreiben, damit Claude Code mit dem (rotierten) Refresh-Token weiterarbeitet
            string p = CredPath();
            Dictionary<string, object> j = J.Parse(Files.ReadText(p));
            Dictionary<string, object> o = J.Obj(j, "claudeAiOauth");
            if (o != null)
            {
                o["accessToken"] = access;
                o["refreshToken"] = refresh;
                o["expiresAt"] = expiresMs;
                Files.WriteTextAtomic(p, J.Serialize(j));
            }
            Log.Write("Claude: Token erneuert");
            Creds n = new Creds();
            n.Access = access; n.Refresh = refresh; n.SubscriptionType = fresh.SubscriptionType; n.Tier = fresh.Tier;
            n.Expires = DateTime.Now.AddSeconds(expiresIn);
            return n;
        }

        static HttpResult CallUsage(string token)
        {
            return Http.Send("GET", UsageUrl, Http.H(
                "Authorization", "Bearer " + token,
                "anthropic-beta", "oauth-2025-04-20",
                "User-Agent", "claude-code/2.1.0",
                "Content-Type", "application/json"), null, null);
        }

        public override ProviderResult FetchCore()
        {
            Creds c = ReadCreds();
            if (c.Expired) c = RefreshCreds(c);
            HttpResult r = CallUsage(c.Access);
            if (r.Status == 401)
            {
                c = RefreshCreds(c);
                r = CallUsage(c.Access);
            }
            if (r.Status == 403 && (r.Body ?? "").Contains("user:profile"))
                throw new ProviderException("Der Claude-Token hat keine Berechtigung für Nutzungsdaten (z.B. 'claude setup-token'). In 'claude' mit /login neu anmelden.");
            if (!r.Ok) throw Fail(r);

            ProviderResult res = Map(J.Parse(r.Body), NewResult());
            res.Plan = PlanName(c.SubscriptionType, c.Tier);
            return res;
        }

        public static string PlanName(string sub, string tier)
        {
            string plan = string.IsNullOrEmpty(sub) ? null : Fmt.Capitalize(sub);
            string t = (tier ?? "").ToLowerInvariant();
            if (plan == null)
            {
                if (t.Contains("max")) plan = "Max";
                else if (t.Contains("pro")) plan = "Pro";
                else if (t.Contains("team")) plan = "Team";
                else if (t.Contains("enterprise")) plan = "Enterprise";
            }
            if (plan != null && t.Contains("20x")) plan += " 20x";
            else if (plan != null && t.Contains("5x")) plan += " 5x";
            return plan;
        }

        static UsageWindow Win(Dictionary<string, object> j, string key, string label, string shortLabel)
        {
            Dictionary<string, object> w = J.Obj(j, key);
            if (w == null) return null;
            double? util = J.Num(w, "utilization");
            if (!util.HasValue) return null;
            return FromUsedPct(label, shortLabel, util, T.Flexible(J.Get(w, "resets_at")));
        }

        public static ProviderResult Map(Dictionary<string, object> j, ProviderResult res)
        {
            if (j == null) throw new ProviderException("Claude: unerwartete Antwort.");
            UsageWindow five = Win(j, "five_hour", "5 Stunden", "5h");
            UsageWindow week = Win(j, "seven_day", "Woche (alle Modelle)", "Woche");
            if (week == null) week = Win(j, "seven_day_oauth_apps", "Woche", "Woche");
            if (five != null) res.Windows.Add(five);
            if (week != null) res.Windows.Add(week);

            HashSet<string> seen = new HashSet<string>();
            UsageWindow opus = Win(j, "seven_day_opus", "Woche (Opus)", "Opus");
            if (opus != null) { res.Windows.Add(opus); seen.Add("opus"); }
            UsageWindow sonnet = Win(j, "seven_day_sonnet", "Woche (Sonnet)", "Sonnet");
            if (sonnet != null) { res.Windows.Add(sonnet); seen.Add("sonnet"); }

            IList limits = J.Arr(j, "limits");
            if (limits != null)
            {
                foreach (object l in limits)
                {
                    if (J.Str(l, "kind") != "weekly_scoped") continue;
                    double? pct = J.Num(l, "percent");
                    if (!pct.HasValue) continue;
                    string model = J.Str(l, "scope", "model", "display_name");
                    if (string.IsNullOrEmpty(model) || seen.Contains(model.ToLowerInvariant())) continue;
                    seen.Add(model.ToLowerInvariant());
                    res.Windows.Add(FromUsedPct("Woche (" + model + ")", model, pct, T.Flexible(J.Get(l, "resets_at"))));
                }
            }
            foreach (string k in new string[] { "seven_day_routines", "seven_day_cowork" })
            {
                UsageWindow rt = Win(j, k, "Woche (Routinen)", "Routinen");
                if (rt != null) { res.Windows.Add(rt); break; }
            }

            Dictionary<string, object> extra = J.Obj(j, "extra_usage");
            if (extra != null && J.Bool(extra, "is_enabled"))
            {
                double? util = J.Num(extra, "utilization");
                if (util.HasValue)
                {
                    UsageWindow e = FromUsedPct("Extra-Nutzung (Monatsbudget)", "Extra", util, null);
                    res.Windows.Add(e);
                }
            }

            res.BarA = five;
            res.BarB = week;
            if (res.Windows.Count == 0) throw new ProviderException("Claude: keine Limits in der Antwort gefunden.");
            return res;
        }
    }

    // ================================================================== ChatGPT (Codex)
    public class ChatGPTProvider : Provider
    {
        const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
        const string TokenUrl = "https://auth.openai.com/oauth/token";
        const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

        public ChatGPTProvider()
        {
            Id = "chatgpt"; Name = "ChatGPT";
            Brand = System.Drawing.Color.FromArgb(16, 163, 127);
        }

        static string AuthPath()
        {
            string dir = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Files.Home, ".codex");
            return Path.Combine(dir, "auth.json");
        }

        class Creds { public string Access, Refresh, IdToken, AccountId, Email; public DateTime? Expires; }

        static Creds ReadCreds()
        {
            string p = AuthPath();
            if (!File.Exists(p))
                throw new ProviderException("Kein Codex-Login gefunden. Codex CLI installieren (npm i -g @openai/codex), 'codex' starten und 'Sign in with ChatGPT' wählen.", true);
            Dictionary<string, object> j = J.Parse(Files.ReadText(p));
            Dictionary<string, object> t = J.Obj(j, "tokens");
            if (t == null || string.IsNullOrEmpty(J.Str(t, "access_token")))
                throw new ProviderException("Codex ist nicht mit ChatGPT angemeldet (nur API-Key). 'codex login' ausführen und mit ChatGPT anmelden.", true);
            Creds c = new Creds();
            c.Access = J.Str(t, "access_token");
            c.Refresh = J.Str(t, "refresh_token");
            c.IdToken = J.Str(t, "id_token");
            c.AccountId = J.Str(t, "account_id");
            Dictionary<string, object> idp = J.JwtPayload(c.IdToken);
            if (string.IsNullOrEmpty(c.AccountId))
                c.AccountId = J.Str(idp, "https://api.openai.com/auth", "chatgpt_account_id");
            c.Email = J.Str(idp, "email");
            Dictionary<string, object> ap = J.JwtPayload(c.Access);
            double? exp = J.Num(ap, "exp");
            if (exp.HasValue) c.Expires = T.FromUnixSeconds(exp.Value);
            return c;
        }

        Creds RefreshCreds(Creds c)
        {
            if (!Cfg.CodexAllowTokenRefresh || string.IsNullOrEmpty(c.Refresh))
                throw new ProviderException("ChatGPT-Token abgelaufen. Einmal 'codex' starten, dann wird er erneuert.");
            Creds fresh = ReadCreds();
            if (fresh.Access != c.Access && (!fresh.Expires.HasValue || fresh.Expires > DateTime.Now.AddMinutes(1)))
                return fresh;

            string body = J.Serialize(new Dictionary<string, object> {
                { "client_id", ClientId }, { "grant_type", "refresh_token" },
                { "refresh_token", fresh.Refresh }, { "scope", "openid profile email" } });
            HttpResult r = Http.Send("POST", TokenUrl, null, body, "application/json");
            if (!r.Ok)
            {
                Log.Write("Codex Token-Refresh fehlgeschlagen: HTTP " + r.Status + (r.NetworkError ?? ""));
                throw new ProviderException("ChatGPT-Token konnte nicht erneuert werden (" +
                    (r.NetworkError ?? "HTTP " + r.Status) + "). 'codex login' erneut ausführen.");
            }
            Dictionary<string, object> t = J.Parse(r.Body);
            string access = J.Str(t, "access_token");
            if (string.IsNullOrEmpty(access)) throw new ProviderException("ChatGPT-Token-Erneuerung: ungültige Antwort.");

            string p = AuthPath();
            Dictionary<string, object> j = J.Parse(Files.ReadText(p));
            Dictionary<string, object> tok = J.Obj(j, "tokens");
            if (tok != null)
            {
                tok["access_token"] = access;
                string nr = J.Str(t, "refresh_token"); if (!string.IsNullOrEmpty(nr)) tok["refresh_token"] = nr;
                string ni = J.Str(t, "id_token"); if (!string.IsNullOrEmpty(ni)) tok["id_token"] = ni;
                j["last_refresh"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                Files.WriteTextAtomic(p, J.Serialize(j));
            }
            Log.Write("ChatGPT: Token erneuert");
            return ReadCreds();
        }

        static HttpResult CallUsage(Creds c)
        {
            return Http.Send("GET", UsageUrl, Http.H(
                "Authorization", "Bearer " + c.Access,
                "ChatGPT-Account-Id", string.IsNullOrEmpty(c.AccountId) ? null : c.AccountId,
                "User-Agent", "AIUsageMonitor"), null, null);
        }

        public override ProviderResult FetchCore()
        {
            Creds c = ReadCreds();
            if (c.Expires.HasValue && c.Expires.Value <= DateTime.Now.AddSeconds(60)) c = RefreshCreds(c);
            HttpResult r = CallUsage(c);
            if (r.Status == 401)
            {
                c = RefreshCreds(c);
                r = CallUsage(c);
            }
            if (!r.Ok) throw Fail(r);
            ProviderResult res = Map(J.Parse(r.Body), NewResult());
            res.Account = c.Email;
            return res;
        }

        static UsageWindow Win(object w, string prefix)
        {
            if (w == null) return null;
            double? used = J.Num(w, "used_percent");
            if (!used.HasValue) return null;
            double secs = J.Num(w, "limit_window_seconds") ?? 0;
            DateTime? reset = null;
            double? at = J.Num(w, "reset_at");
            if (at.HasValue && at.Value > 0) reset = T.FromUnixSeconds(at.Value);
            else
            {
                double? after = J.Num(w, "reset_after_seconds");
                if (after.HasValue) reset = DateTime.Now.AddSeconds(after.Value);
            }
            string label = secs > 0 ? Fmt.WindowName(secs) : "Limit";
            string shortL = secs > 0 ? Fmt.WindowShort(secs) : "?";
            if (prefix != null) { label = prefix + " · " + label; shortL = prefix + " " + shortL; }
            return FromUsedPct(label, shortL, used, reset);
        }

        public static ProviderResult Map(Dictionary<string, object> j, ProviderResult res)
        {
            if (j == null) throw new ProviderException("ChatGPT: unerwartete Antwort.");
            string plan = J.Str(j, "plan_type");
            if (!string.IsNullOrEmpty(plan)) res.Plan = Fmt.Capitalize(plan);

            Dictionary<string, object> rl = J.Obj(j, "rate_limit");
            UsageWindow primary = Win(J.Get(rl, "primary_window"), null);
            UsageWindow secondary = Win(J.Get(rl, "secondary_window"), null);
            if (primary != null) res.Windows.Add(primary);
            if (secondary != null) res.Windows.Add(secondary);

            IList extra = J.Arr(j, "additional_rate_limits");
            if (extra != null)
            {
                foreach (object e in extra)
                {
                    string name = J.Str(e, "limit_name") ?? J.Str(e, "metered_feature") ?? "Zusatz";
                    UsageWindow a = Win(J.Get(e, "rate_limit", "primary_window"), name);
                    UsageWindow b = Win(J.Get(e, "rate_limit", "secondary_window"), name);
                    if (a != null) res.Windows.Add(a);
                    if (b != null) res.Windows.Add(b);
                }
            }

            Dictionary<string, object> credits = J.Obj(j, "credits");
            if (credits != null && J.Bool(credits, "has_credits") && !J.Bool(credits, "unlimited"))
            {
                double? bal = J.Num(credits, "balance");
                if (bal.HasValue)
                {
                    UsageWindow cw = new UsageWindow("Credits", "Credits");
                    cw.Note = "Guthaben: " + bal.Value.ToString("0.##", Fmt.De);
                    res.Windows.Add(cw);
                }
            }

            res.BarA = primary;
            res.BarB = secondary;
            if (primary == null && secondary == null)
            {
                if (J.Bool(rl, "limit_reached"))
                    throw new ProviderException("ChatGPT: Limit erreicht (keine Detaildaten).");
                throw new ProviderException("ChatGPT: keine Limits in der Antwort (Plan: " + (plan ?? "?") + ").");
            }
            return res;
        }
    }

    // ================================================================== Gemini (über Antigravity)
    // Die Gemini CLI bedient Privatkonten (kostenlos, AI Pro, AI Ultra) seit dem 18.06.2026 nicht mehr.
    // Kontingente kommen deshalb aus der Antigravity CLI ("agy"), siehe Antigravity.cs.
    public class GeminiProvider : Provider
    {
        const string AgyHint = "Gemini läuft für Privatkonten über Antigravity. Antigravity CLI installieren (PowerShell: irm https://antigravity.google/cli/install.ps1 | iex), neues Terminal öffnen, 'agy' starten und mit Google anmelden.";

        public GeminiProvider()
        {
            Id = "gemini"; Name = "Gemini";
            Brand = System.Drawing.Color.FromArgb(66, 133, 244);
            MinRefreshMinutes = 5; // jeder Abruf startet die Antigravity CLI – nicht jede Minute
        }

        public override ProviderResult FetchCore()
        {
            string agy = Antigravity.FindAgy();
            if (agy == null)
            {
                Log.Write("Gemini: agy nicht gefunden (gesucht: ANTIGRAVITY_CLI_PATH, ~\\.local\\bin, WinGet, Chocolatey, PATH)");
                throw new ProviderException(AgyHint, true);
            }
            return Antigravity.FetchUsage(agy, NewResult());
        }
    }

    // ================================================================== GitHub Copilot
    public class CopilotProvider : Provider
    {
        public const string VsCodeClientId = "Iv1.b507a08c87ecfe98";
        string workingToken;
        string lastConfigToken;

        public CopilotProvider()
        {
            Id = "copilot"; Name = "Copilot";
            Brand = System.Drawing.Color.FromArgb(137, 87, 229);
        }

        List<KeyValuePair<string, string>> TokenCandidates()
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(Cfg.CopilotToken))
                list.Add(new KeyValuePair<string, string>("Monitor-Login", Cfg.CopilotToken));
            string env = Environment.GetEnvironmentVariable("GH_COPILOT_TOKEN");
            if (!string.IsNullOrEmpty(env)) list.Add(new KeyValuePair<string, string>("GH_COPILOT_TOKEN", env));

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string f in new string[] { "apps.json", "hosts.json" })
            {
                try
                {
                    string p = Path.Combine(Path.Combine(local, "github-copilot"), f);
                    if (!File.Exists(p)) continue;
                    Dictionary<string, object> j = J.Parse(Files.ReadText(p));
                    if (j == null) continue;
                    foreach (KeyValuePair<string, object> kv in j)
                    {
                        if (!kv.Key.StartsWith("github.com", StringComparison.OrdinalIgnoreCase)) continue;
                        string t = J.Str(kv.Value, "oauth_token");
                        if (!string.IsNullOrEmpty(t)) list.Add(new KeyValuePair<string, string>("github-copilot\\" + f, t));
                    }
                }
                catch { }
            }

            string gh = GhToken();
            if (!string.IsNullOrEmpty(gh)) list.Add(new KeyValuePair<string, string>("gh CLI", gh));
            return list;
        }

        static string GhToken()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("gh", "auth token");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
                    o = (o ?? "").Trim();
                    return p.ExitCode == 0 && o.Length > 0 ? o : null;
                }
            }
            catch { return null; }
        }

        static HttpResult Call(string token)
        {
            return Http.Send("GET", "https://api.github.com/copilot_internal/user", Http.H(
                "Authorization", "token " + token,
                "Editor-Version", "vscode/1.96.2",
                "Editor-Plugin-Version", "copilot-chat/0.26.7",
                "User-Agent", "GitHubCopilotChat/0.26.7",
                "X-Github-Api-Version", "2025-04-01"), null, null);
        }

        public override ProviderResult FetchCore()
        {
            // Bewährten Token zuerst – dann muss nicht jedes Mal "gh" gestartet werden
            // (außer nach einer neuen Anmeldung über den Monitor: dann den neuen Token nehmen)
            bool newLogin = !string.IsNullOrEmpty(Cfg.CopilotToken) && Cfg.CopilotToken != workingToken && Cfg.CopilotToken != lastConfigToken;
            lastConfigToken = Cfg.CopilotToken;
            if (workingToken != null && !newLogin)
            {
                HttpResult r0 = Call(workingToken);
                if (r0.Ok) return Map(J.Parse(r0.Body), NewResult());
                if (r0.NetworkError != null) throw new ProviderException("Copilot: " + Http.Describe(r0));
            }
            List<KeyValuePair<string, string>> cands = TokenCandidates();
            if (cands.Count == 0)
                throw new ProviderException("Nicht mit GitHub verbunden. Rechtsklick auf ein Symbol → 'GitHub Copilot anmelden…'.", true);

            HttpResult last = null;
            HashSet<string> tried = new HashSet<string>();
            foreach (KeyValuePair<string, string> c in cands)
            {
                if (!tried.Add(c.Value)) continue;
                HttpResult r = Call(c.Value);
                if (r.Ok)
                {
                    workingToken = c.Value;
                    return Map(J.Parse(r.Body), NewResult());
                }
                Log.Write("Copilot: Token aus " + c.Key + " -> HTTP " + r.Status);
                last = r;
                if (r.NetworkError != null) break;
            }
            workingToken = null;
            if (last != null && (last.Status == 401 || last.Status == 403 || last.Status == 404))
                throw new ProviderException("GitHub-Token wird für Copilot abgelehnt (" + last.Status + "). Rechtsklick → 'GitHub Copilot anmelden…'.");
            if (last != null && last.Status == 429) throw Fail(last);
            throw new ProviderException("Copilot: " + Http.Describe(last));
        }

        static UsageWindow Quota(Dictionary<string, object> snaps, string key, string label, string shortL, DateTime? reset)
        {
            Dictionary<string, object> s = J.Obj(snaps, key);
            if (s == null) return null;
            UsageWindow w = new UsageWindow(label, shortL);
            w.ResetsAt = reset;
            w.ResetIsDateOnly = true;
            if (J.Bool(s, "unlimited"))
            {
                w.Unlimited = true;
                w.RemainingPct = 100;
                w.Note = "unbegrenzt";
                return w;
            }
            double? pct = J.Num(s, "percent_remaining");
            double? ent = J.Num(s, "entitlement");
            double? rem = J.Num(s, "remaining");
            if (!pct.HasValue && ent.HasValue && rem.HasValue && ent.Value > 0) pct = rem.Value / ent.Value * 100;
            if (!pct.HasValue) return null;
            if (ent.HasValue && ent.Value <= 0 && (!rem.HasValue || rem.Value <= 0) && pct.Value <= 0) return null; // Platzhalter
            w.RemainingPct = Math.Max(0, Math.Min(100, pct.Value));
            if (ent.HasValue && rem.HasValue && ent.Value > 0)
                w.Note = Math.Max(0, rem.Value).ToString("0", Fmt.De) + " von " + ent.Value.ToString("0", Fmt.De) + " übrig";
            double? over = J.Num(s, "overage_count");
            if (over.HasValue && over.Value > 0)
                w.Note = (w.Note == null ? "" : w.Note + " · ") + over.Value.ToString("0", Fmt.De) + " über Kontingent";
            return w;
        }

        public static ProviderResult Map(Dictionary<string, object> j, ProviderResult res)
        {
            if (j == null) throw new ProviderException("Copilot: unerwartete Antwort.");
            if (J.Str(j, "access_type_sku") == "subscription_ended")
                throw new ProviderException("Copilot-Abo ist beendet.");
            string plan = J.Str(j, "copilot_plan");
            if (!string.IsNullOrEmpty(plan)) res.Plan = Fmt.Capitalize(plan);
            res.Account = J.Str(j, "login");

            DateTime? reset = null;
            string rd = J.Str(j, "quota_reset_date_utc") ?? J.Str(j, "quota_reset_date");
            if (!string.IsNullOrEmpty(rd))
            {
                DateTime d;
                if (rd.Length <= 10 && DateTime.TryParseExact(rd, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out d))
                    reset = DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();
                else reset = T.ParseIso(rd);
            }

            Dictionary<string, object> snaps = J.Obj(j, "quota_snapshots");
            UsageWindow premium = Quota(snaps, "premium_interactions", "Premium-Requests (Monat)", "Premium", reset);
            UsageWindow chat = Quota(snaps, "chat", "Chat (Monat)", "Chat", reset);
            UsageWindow compl = Quota(snaps, "completions", "Code-Vervollständigung (Monat)", "Compl.", reset);
            if (premium != null) res.Windows.Add(premium);
            if (chat != null) res.Windows.Add(chat);
            if (compl != null) res.Windows.Add(compl);

            // Free-Plan: limited_user_quotas / monthly_quotas
            if (res.Windows.Count == 0)
            {
                Dictionary<string, object> lim = J.Obj(j, "limited_user_quotas");
                Dictionary<string, object> mon = J.Obj(j, "monthly_quotas");
                DateTime? lreset = T.Flexible(J.Get(j, "limited_user_reset_date")) ?? reset;
                foreach (string k in new string[] { "chat", "completions" })
                {
                    double? rem = J.Num(lim, k), tot = J.Num(mon, k);
                    if (!rem.HasValue || !tot.HasValue || tot.Value <= 0) continue;
                    UsageWindow w = new UsageWindow(k == "chat" ? "Chat (Monat)" : "Code-Vervollständigung (Monat)", k == "chat" ? "Chat" : "Compl.");
                    w.RemainingPct = Math.Max(0, Math.Min(100, rem.Value / tot.Value * 100));
                    w.Note = rem.Value.ToString("0", Fmt.De) + " von " + tot.Value.ToString("0", Fmt.De) + " übrig";
                    w.ResetsAt = lreset; w.ResetIsDateOnly = true;
                    res.Windows.Add(w);
                    if (k == "chat") chat = w; else compl = w;
                }
            }

            res.BarA = premium ?? chat ?? compl;
            res.BarB = null;
            foreach (UsageWindow cand in new UsageWindow[] { chat, compl })
                if (cand != null && cand != res.BarA && !cand.Unlimited) { res.BarB = cand; break; }
            if (res.Windows.Count == 0)
            {
                if (J.Bool(j, "token_based_billing"))
                {
                    UsageWindow tb = new UsageWindow("Abrechnung", "Token");
                    tb.Note = "Token-basierte Abrechnung – kein festes Kontingent";
                    res.Windows.Add(tb);
                    return res;
                }
                throw new ProviderException("Copilot: keine Kontingente in der Antwort.");
            }
            return res;
        }
    }

    // ================================================================== GitHub Device-Flow (Copilot-Login)
    public class DeviceCode
    {
        public string Code, UserCode, VerificationUri;
        public int Interval = 5;
        public DateTime ExpiresAt;
    }

    public static class GitHubDeviceFlow
    {
        public static DeviceCode Start()
        {
            HttpResult r = Http.Send("POST", "https://github.com/login/device/code", null,
                Http.Form("client_id", CopilotProvider.VsCodeClientId, "scope", "read:user"),
                "application/x-www-form-urlencoded");
            if (!r.Ok) throw new ProviderException("GitHub-Login konnte nicht gestartet werden: " + Http.Describe(r));
            Dictionary<string, object> j = J.Parse(r.Body);
            DeviceCode d = new DeviceCode();
            d.Code = J.Str(j, "device_code");
            d.UserCode = J.Str(j, "user_code");
            d.VerificationUri = J.Str(j, "verification_uri") ?? "https://github.com/login/device";
            d.Interval = (int)(J.Num(j, "interval") ?? 5);
            d.ExpiresAt = DateTime.Now.AddSeconds(J.Num(j, "expires_in") ?? 900);
            if (d.Code == null || d.UserCode == null) throw new ProviderException("GitHub-Login: ungültige Antwort.");
            return d;
        }

        // Rückgabe: Token, null = noch ausstehend; Exception = abgebrochen/Fehler
        public static string Poll(DeviceCode d)
        {
            HttpResult r = Http.Send("POST", "https://github.com/login/oauth/access_token", null,
                Http.Form("client_id", CopilotProvider.VsCodeClientId, "device_code", d.Code,
                          "grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                "application/x-www-form-urlencoded");
            if (r.NetworkError != null) return null;
            Dictionary<string, object> j = J.Parse(r.Body);
            string token = J.Str(j, "access_token");
            if (!string.IsNullOrEmpty(token)) return token;
            string err = J.Str(j, "error");
            if (err == "authorization_pending") return null;
            if (err == "slow_down") { d.Interval += 5; return null; }
            if (err == "expired_token") throw new ProviderException("Der Code ist abgelaufen. Bitte erneut versuchen.");
            if (err == "access_denied") throw new ProviderException("Anmeldung wurde abgelehnt.");
            throw new ProviderException("GitHub-Login fehlgeschlagen: " + (err ?? Http.Describe(r)));
        }
    }
}
