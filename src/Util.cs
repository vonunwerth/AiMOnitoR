// AI Usage Monitor - Hilfsfunktionen (HTTP, JSON, Konfiguration, Log, Formatierung)
// Zielplattform: .NET Framework 4.x, C# 5 (eingebauter Windows-Compiler csc.exe)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace AIUsageMonitor
{
    // ------------------------------------------------------------------ Datenmodell
    public class UsageWindow
    {
        public string Label;          // z.B. "5 Stunden"
        public string ShortLabel;     // z.B. "5h" (für Tooltip)
        public double? RemainingPct;  // 0..100, null = unbekannt
        public DateTime? ResetsAt;    // lokale Zeit
        public bool ResetIsDateOnly;
        public string Note;           // Zusatzinfo, z.B. "212 / 300 übrig"
        public bool Unlimited;

        public UsageWindow(string label, string shortLabel)
        {
            Label = label;
            ShortLabel = shortLabel;
        }
    }

    public class ProviderResult
    {
        public string Id;
        public string Name;
        public string Plan;
        public string Account;
        public List<UsageWindow> Windows = new List<UsageWindow>();
        public UsageWindow BarA;   // linker Balken im Tray-Icon
        public UsageWindow BarB;   // rechter Balken im Tray-Icon (optional)
        public string Error;       // Fehler beim letzten Abruf
        public bool NotConfigured; // kein Login gefunden
        public bool Stale;         // Daten stammen von einem früheren Abruf
        public bool RateLimited;   // Anbieter meldet zu viele Abfragen (429)
        public int PauseMinutes;   // >0: Anbieter so lange nicht automatisch abfragen
        public DateTime? UpdatedAt;
        public bool Loading;
    }

    public class ProviderException : Exception
    {
        public bool NotConfigured;
        public bool RateLimited;
        public int PauseMinutes;
        public ProviderException(string msg) : base(msg) { }
        public ProviderException(string msg, bool notConfigured) : base(msg) { NotConfigured = notConfigured; }
    }

    // ------------------------------------------------------------------ JSON
    public static class J
    {
        static JavaScriptSerializer NewSerializer()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 256;
            return s;
        }

        public static Dictionary<string, object> Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return NewSerializer().DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        public static string Serialize(object o)
        {
            return NewSerializer().Serialize(o);
        }

        public static object Get(object o, params string[] path)
        {
            object cur = o;
            foreach (string p in path)
            {
                Dictionary<string, object> d = cur as Dictionary<string, object>;
                if (d == null) return null;
                object next;
                if (!d.TryGetValue(p, out next)) return null;
                cur = next;
            }
            return cur;
        }

        public static Dictionary<string, object> Obj(object o, params string[] path)
        {
            return Get(o, path) as Dictionary<string, object>;
        }

        public static string Str(object o, params string[] path)
        {
            object v = Get(o, path);
            if (v == null) return null;
            if (v is string) return (string)v;
            if (v is IDictionary || v is IList) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double? Num(object o, params string[] path)
        {
            object v = Get(o, path);
            return ToNum(v);
        }

        public static double? ToNum(object v)
        {
            if (v == null) return null;
            if (v is bool) return null;
            if (v is string)
            {
                double d;
                if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                return null;
            }
            if (v is IDictionary || v is IList) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        public static bool Bool(object o, params string[] path)
        {
            object v = Get(o, path);
            if (v is bool) return (bool)v;
            if (v is string) return ((string)v).Equals("true", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        public static IList Arr(object o, params string[] path)
        {
            return Get(o, path) as IList;
        }

        // JWT-Payload (ohne Signaturprüfung) – nur zum Auslesen von exp/email
        public static Dictionary<string, object> JwtPayload(string jwt)
        {
            if (string.IsNullOrEmpty(jwt)) return null;
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            try
            {
                string b = parts[1].Replace('-', '+').Replace('_', '/');
                while (b.Length % 4 != 0) b += "=";
                return Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b)));
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ Zeit
    public static class T
    {
        public static DateTime? ParseIso(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out dto))
                return dto.LocalDateTime;
            return null;
        }

        public static DateTime FromUnixSeconds(double secs)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(secs).ToLocalTime();
        }

        public static double NowUnixSeconds()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        // Zeitstempel kann Sekunden, Millisekunden, ISO-String sein
        public static DateTime? Flexible(object v)
        {
            if (v == null) return null;
            if (v is string)
            {
                DateTime? iso = ParseIso((string)v);
                if (iso.HasValue) return iso;
            }
            double? n = J.ToNum(v);
            if (!n.HasValue || n.Value <= 0) return null;
            double secs = n.Value > 1e11 ? n.Value / 1000.0 : n.Value;
            return FromUnixSeconds(secs);
        }
    }

    // ------------------------------------------------------------------ HTTP
    public class HttpResult
    {
        public int Status;
        public string Body;
        public string NetworkError;
        public bool Ok { get { return Status >= 200 && Status < 300; } }
    }

    public static class Http
    {
        static Http()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.Expect100Continue = false;
            }
            catch { }
        }

        public static HttpResult Send(string method, string url, Dictionary<string, string> headers,
            string body, string contentType)
        {
            HttpResult r = new HttpResult();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.Accept = "application/json";
                req.UserAgent = "AIUsageMonitor/1.0";
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> h in headers)
                    {
                        if (h.Value == null) continue;
                        string k = h.Key.ToLowerInvariant();
                        if (k == "user-agent") req.UserAgent = h.Value;
                        else if (k == "accept") req.Accept = h.Value;
                        else if (k == "content-type") req.ContentType = h.Value;
                        else req.Headers[h.Key] = h.Value;
                    }
                }
                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentType = contentType ?? "application/json";
                    req.ContentLength = data.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    r.Status = (int)resp.StatusCode;
                    r.Body = ReadBody(resp);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    r.Status = (int)resp.StatusCode;
                    try { r.Body = ReadBody(resp); } catch { }
                    resp.Close();
                }
                else
                {
                    r.NetworkError = ex.Message;
                }
            }
            catch (Exception ex)
            {
                r.NetworkError = ex.Message;
            }
            return r;
        }

        static string ReadBody(HttpWebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static string Form(params string[] kv)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv[i])).Append('=').Append(Uri.EscapeDataString(kv[i + 1] ?? ""));
            }
            return sb.ToString();
        }

        public static Dictionary<string, string> H(params string[] kv)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        // Kurze, ungefährliche Fehlerbeschreibung (keine Tokens)
        public static string Describe(HttpResult r)
        {
            if (r.NetworkError != null) return "Netzwerkfehler: " + r.NetworkError;
            string snippet = r.Body ?? "";
            Dictionary<string, object> j = J.Parse(snippet);
            if (j != null)
            {
                string msg = J.Str(j, "error", "message") ?? J.Str(j, "error_description")
                    ?? J.Str(j, "message") ?? J.Str(j, "detail") ?? J.Str(j, "error");
                if (msg != null) snippet = msg;
            }
            if (snippet.Length > 160) snippet = snippet.Substring(0, 160) + "…";
            return "HTTP " + r.Status + (snippet.Length > 0 ? ": " + snippet : "");
        }
    }

    // ------------------------------------------------------------------ Dateien
    public static class Files
    {
        public static string Home
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        }

        public static string AppDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIUsageMonitor");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string ReadText(string path)
        {
            // FileShare.ReadWrite: die CLIs dürfen die Datei gleichzeitig offen haben
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                return sr.ReadToEnd();
        }

        // Atomar schreiben (Temp-Datei + Ersetzen), UTF-8 ohne BOM
        public static void WriteTextAtomic(string path, string content)
        {
            string tmp = path + ".aiusage.tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
            }
            catch
            {
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    public static class Log
    {
        static readonly object Lock = new object();
        static string path;

        // Log liegt neben der EXE (log.txt); falls dort nicht schreibbar, unter %APPDATA%\AIUsageMonitor
        public static string FilePath
        {
            get
            {
                if (path != null) return path;
                try
                {
                    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    string p = Path.Combine(dir, "log.txt");
                    File.AppendAllText(p, "");
                    path = p;
                }
                catch { path = Path.Combine(Files.AppDir, "log.txt"); }
                return path;
            }
        }

        public static void Write(string msg)
        {
            try
            {
                lock (Lock)
                {
                    string p = FilePath;
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = p + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(p, old);
                    }
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Konfiguration
    public class Config
    {
        public int RefreshMinutes = 1;
        public bool Claude = true, ChatGPT = true, Gemini = true, Copilot = true;
        public bool ClaudeAllowTokenRefresh = true;
        public bool CodexAllowTokenRefresh = true;
        public string CopilotToken = "";
        public int WarnBelowPercent = 25;
        public int CriticalBelowPercent = 10;

        public static string FilePath { get { return Path.Combine(Files.AppDir, "config.json"); } }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(FilePath)) { c.Save(); return c; }
                Dictionary<string, object> j = J.Parse(Files.ReadText(FilePath));
                if (j == null) return c;
                double? n = J.Num(j, "refreshMinutes");
                if (n.HasValue) c.RefreshMinutes = Math.Max(1, (int)n.Value);
                // Migration: alte Konfiguration (Standard 5 Minuten) auf 1 Minute umstellen
                bool migrate = !J.Num(j, "configVersion").HasValue;
                if (migrate && c.RefreshMinutes == 5) c.RefreshMinutes = 1;
                n = J.Num(j, "warnBelowPercent"); if (n.HasValue) c.WarnBelowPercent = (int)n.Value;
                n = J.Num(j, "criticalBelowPercent"); if (n.HasValue) c.CriticalBelowPercent = (int)n.Value;
                Dictionary<string, object> p = J.Obj(j, "providers");
                if (p != null)
                {
                    if (J.Get(p, "claude") is bool) c.Claude = J.Bool(p, "claude");
                    if (J.Get(p, "chatgpt") is bool) c.ChatGPT = J.Bool(p, "chatgpt");
                    if (J.Get(p, "gemini") is bool) c.Gemini = J.Bool(p, "gemini");
                    if (J.Get(p, "copilot") is bool) c.Copilot = J.Bool(p, "copilot");
                }
                if (J.Get(j, "claudeAllowTokenRefresh") is bool) c.ClaudeAllowTokenRefresh = J.Bool(j, "claudeAllowTokenRefresh");
                if (J.Get(j, "codexAllowTokenRefresh") is bool) c.CodexAllowTokenRefresh = J.Bool(j, "codexAllowTokenRefresh");
                c.CopilotToken = Unprotect(J.Str(j, "copilotToken") ?? "");
                if (migrate) c.Save();
            }
            catch (Exception ex) { Log.Write("Config laden fehlgeschlagen: " + ex.Message); }
            return c;
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"configVersion\": 2,");
            sb.AppendLine("  \"refreshMinutes\": " + RefreshMinutes + ",");
            sb.AppendLine("  \"providers\": {");
            sb.AppendLine("    \"claude\": " + B(Claude) + ",");
            sb.AppendLine("    \"chatgpt\": " + B(ChatGPT) + ",");
            sb.AppendLine("    \"gemini\": " + B(Gemini) + ",");
            sb.AppendLine("    \"copilot\": " + B(Copilot));
            sb.AppendLine("  },");
            sb.AppendLine("  \"claudeAllowTokenRefresh\": " + B(ClaudeAllowTokenRefresh) + ",");
            sb.AppendLine("  \"codexAllowTokenRefresh\": " + B(CodexAllowTokenRefresh) + ",");
            sb.AppendLine("  \"copilotToken\": " + J.Serialize(Protect(CopilotToken ?? "")));
            sb.AppendLine("}");
            try { Files.WriteTextAtomic(FilePath, sb.ToString()); }
            catch (Exception ex) { Log.Write("Config speichern fehlgeschlagen: " + ex.Message); }
        }

        static string B(bool b) { return b ? "true" : "false"; }

        // Token mit Windows-DPAPI (nur dieses Benutzerkonto) verschlüsselt ablegen
        static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] enc = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(enc);
            }
            catch { return plain; }
        }

        static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dpapi:")) return stored ?? "";
            try
            {
                byte[] dec = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }
    }

    // ------------------------------------------------------------------ Formatierung (Deutsch)
    public static class Fmt
    {
        public static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

        public static string Pct(double? v)
        {
            if (!v.HasValue) return "–";
            return Math.Round(v.Value).ToString("0", De) + " %";
        }

        public static string Duration(TimeSpan ts)
        {
            if (ts.TotalSeconds < 60) return "< 1 Min.";
            int d = ts.Days, h = ts.Hours, m = ts.Minutes;
            if (d > 0) return d + " T. " + h + " Std.";
            if (h > 0) return h + " Std. " + m + " Min.";
            return m + " Min.";
        }

        public static string When(DateTime t, bool dateOnly)
        {
            DateTime today = DateTime.Today;
            string time = t.ToString("HH:mm", De);
            if (dateOnly) return t.ToString("ddd dd.MM.", De);
            if (t.Date == today) return "heute " + time;
            if (t.Date == today.AddDays(1)) return "morgen " + time;
            if ((t.Date - today).TotalDays < 7) return t.ToString("ddd", De) + " " + time;
            return t.ToString("ddd dd.MM.", De) + " " + time;
        }

        public static string Reset(UsageWindow w)
        {
            if (!w.ResetsAt.HasValue) return null;
            TimeSpan left = w.ResetsAt.Value - DateTime.Now;
            if (left.TotalSeconds <= 0) return "Zurückgesetzt – wird aktualisiert";
            return "Reset in " + Duration(left) + "  ·  " + When(w.ResetsAt.Value, w.ResetIsDateOnly);
        }

        public static string WindowName(double seconds)
        {
            int s = (int)Math.Round(seconds);
            if (s == 18000) return "5 Stunden";
            if (s == 604800) return "Woche";
            if (s == 86400) return "Tag";
            if (s % 86400 == 0) return (s / 86400) + " Tage";
            if (s % 3600 == 0) return (s / 3600) + " Stunden";
            return (s / 60) + " Minuten";
        }

        public static string WindowShort(double seconds)
        {
            int s = (int)Math.Round(seconds);
            if (s == 604800) return "Woche";
            if (s % 86400 == 0) return (s / 86400) + "T";
            if (s % 3600 == 0) return (s / 3600) + "h";
            return (s / 60) + "m";
        }

        public static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace('_', ' ');
            return char.ToUpper(s[0], De) + s.Substring(1);
        }
    }
}
