// AI Usage Monitor - Gemini über die Antigravity CLI ("agy")
//
// Seit dem 18.06.2026 bedient Google die Gemini CLI für Privatkonten (kostenlos, AI Pro, AI Ultra)
// nicht mehr; Nachfolger ist Antigravity. Dessen CLI liefert mit
//     agy -p /usage --output-format json
// einen maschinenlesbaren Kontingent-Bericht (5-Stunden- und Wochenlimit je Modellgruppe),
// ohne dass ein Modell-Prompt gesendet wird. Login & Token-Erneuerung erledigt agy selbst.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AIUsageMonitor
{
    public static class Antigravity
    {
        public static string FindAgy()
        {
            string env = Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_PATH");
            if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates = {
                Path.Combine(Files.Home, ".local", "bin", "agy.exe"),
                Path.Combine(local, "Programs", "antigravity-cli", "agy.exe"),
                Path.Combine(local, "Programs", "Antigravity CLI", "agy.exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", "agy.exe"),
                @"C:\ProgramData\chocolatey\bin\agy.exe",
            };
            foreach (string c in candidates) if (File.Exists(c)) return c;
            // im PATH suchen – auch im aktuellen PATH aus der Registry, falls agy nach dem Start des Monitors installiert wurde
            string path = (Environment.GetEnvironmentVariable("PATH") ?? "") + ";" +
                (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "") + ";" +
                (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "");
            foreach (string dir in path.Split(';'))
            {
                try
                {
                    if (dir.Trim().Length == 0) continue;
                    foreach (string name in new string[] { "agy.exe", "agy.cmd", "agy.bat" })
                    {
                        string p = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), name);
                        if (File.Exists(p)) return p;
                    }
                }
                catch { }
            }
            return null;
        }

        public class RunResult { public int ExitCode = -1; public string Stdout = "", Stderr = ""; public bool TimedOut; }

        public static RunResult Run(string exe, string args, int timeoutMs)
        {
            RunResult rr = new RunResult();
            string work = Path.Combine(Path.GetTempPath(), "aiusage-agy");
            try { Directory.CreateDirectory(work); } catch { }

            ProcessStartInfo psi;
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + exe + "\" " + args + "\"");
            else
                psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = work;
            psi.EnvironmentVariables["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true"; // Abfrage soll kein Update anstoßen
            psi.EnvironmentVariables["NO_COLOR"] = "1";

            IntPtr job = Job.Create();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.Start();
                Job.Assign(job, p);
                try { p.StandardInput.Close(); } catch { }
                Task<string> outT = p.StandardOutput.ReadToEndAsync();
                Task<string> errT = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    rr.TimedOut = true;
                    try { p.Kill(); } catch { }
                }
                else rr.ExitCode = p.ExitCode;
                try { if (outT.Wait(3000)) rr.Stdout = outT.Result ?? ""; } catch { }
                try { if (errT.Wait(3000)) rr.Stderr = errT.Result ?? ""; } catch { }
            }
            Job.Close(job); // beendet auch alle von agy gestarteten Unterprozesse
            return rr;
        }

        // Kontingent-Bericht abrufen und auswerten
        public static ProviderResult FetchUsage(string agy, ProviderResult res)
        {
            Log.Write("Gemini: starte " + agy);
            RunResult r = Run(agy, "-p /usage --output-format json --print-timeout 90s", 100000);
            Log.Write("Gemini: agy exit " + r.ExitCode + (r.TimedOut ? " (Timeout)" : "") + ", stdout: " + Trim(r.Stdout, 600) + " | stderr: " + Trim(r.Stderr, 400));
            if (r.TimedOut)
                throw new ProviderException("Antigravity CLI hat nicht rechtzeitig geantwortet.");
            Dictionary<string, object> j = ExtractJson(r.Stdout);
            if (j == null)
            {
                string all = (r.Stdout + " " + r.Stderr).ToLowerInvariant();
                ProviderException e;
                if (all.Contains("trust"))
                    e = new ProviderException("Antigravity fragt nach einer Ordnerfreigabe und kann im Hintergrund nicht antworten (Details im Log).");
                else if (all.Contains("login") || all.Contains("sign in") || all.Contains("signin") || all.Contains("auth"))
                    e = new ProviderException("Antigravity ist nicht angemeldet. Im Terminal 'agy' starten und mit Google anmelden.");
                else if (all.Contains("unknown") || all.Contains("flag provided but not defined") || all.Contains("usage:"))
                    e = new ProviderException("Die installierte Antigravity CLI ist zu alt für Nutzungsberichte. Im Terminal 'agy update' ausführen.");
                else
                    e = new ProviderException("Antigravity CLI lieferte keinen Nutzungsbericht (Exit-Code " + r.ExitCode + ").");
                e.PauseMinutes = 15; // nicht jede Minute einen fehlschlagenden Prozess starten
                throw e;
            }
            return Map(j, res);
        }

        static string Trim(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        public static Dictionary<string, object> ExtractJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            Dictionary<string, object> j = J.Parse(s.Trim());
            if (j != null) return j;
            int a = s.IndexOf('{'), b = s.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            return J.Parse(s.Substring(a, b - a + 1));
        }

        static object Pick(object o, string snake, string camel)
        {
            object v = J.Get(o, snake);
            return v ?? J.Get(o, camel);
        }

        static string GroupLabel(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Kontingent";
            string n = name.ToLowerInvariant();
            if (n == "gemini models") return "Gemini";
            if (n.Contains("claude") && n.Contains("gpt")) return "Claude & GPT";
            return name;
        }

        static bool IsFiveHour(string window, string id)
        {
            string w = ((window ?? "") + " " + (id ?? "")).ToLowerInvariant();
            return w.Contains("5h") || w.Contains("five") || w.Contains("5_hour") || w.Contains("5-hour") || w.Contains("session");
        }

        static bool IsWeekly(string window, string id)
        {
            string w = ((window ?? "") + " " + (id ?? "")).ToLowerInvariant();
            return w.Contains("week") || w.Contains("7d");
        }

        public static ProviderResult Map(Dictionary<string, object> j, ProviderResult res)
        {
            string status = J.Str(j, "status");
            if (status != null && status != "SUCCESS")
            {
                ProviderException e = new ProviderException("Antigravity-Nutzungsbericht fehlgeschlagen (" + status + "). Im Terminal 'agy' starten und Anmeldung prüfen.");
                e.PauseMinutes = 15;
                throw e;
            }
            object data = J.Get(j, "command", "data");
            if (data == null) data = Pick(j, "response", "summary");
            if (data == null) data = j;
            IList groups = J.Arr(data, "groups");
            if (groups == null || groups.Count == 0)
                throw new ProviderException("Antigravity-Bericht enthält keine Kontingente.");

            UsageWindow gem5 = null, gemW = null, first5 = null, firstW = null;
            foreach (object g in groups)
            {
                string gname = (string)(Pick(g, "display_name", "displayName") as string) ?? J.Str(g, "name");
                string label = GroupLabel(gname);
                bool isGemini = (gname ?? "").ToLowerInvariant().Contains("gemini");
                IList buckets = J.Arr(g, "buckets");
                if (buckets == null) continue;

                List<UsageWindow> ordered = new List<UsageWindow>();
                foreach (object b in buckets)
                {
                    if (J.Bool(b, "disabled")) continue;
                    double? frac = J.ToNum(Pick(b, "remaining_fraction", "remainingFraction"));
                    if (!frac.HasValue) frac = J.ToNum(J.Get(Pick(b, "remaining", "remaining"), "remainingFraction"));
                    if (!frac.HasValue) continue;
                    string window = J.Str(b, "window");
                    string id = (Pick(b, "bucket_id", "bucketId") as string) ?? J.Str(b, "id");
                    bool five = IsFiveHour(window, id), week = IsWeekly(window, id);
                    string wl = five ? "5 Stunden" : week ? "Woche" : ((Pick(b, "display_name", "displayName") as string) ?? J.Str(b, "name") ?? window ?? "Limit");
                    UsageWindow w = new UsageWindow(label + " · " + wl, five ? "5h" : week ? "Woche" : wl);
                    w.RemainingPct = Math.Max(0, Math.Min(100, frac.Value * 100));
                    w.ResetsAt = T.Flexible(Pick(b, "reset_time", "resetTime"));
                    if (five) ordered.Insert(0, w); else ordered.Add(w);
                    if (five) { if (isGemini && gem5 == null) gem5 = w; if (first5 == null) first5 = w; }
                    if (week) { if (isGemini && gemW == null) gemW = w; if (firstW == null) firstW = w; }
                }
                // Gemini-Gruppe zuerst anzeigen
                if (isGemini) res.Windows.InsertRange(0, ordered); else res.Windows.AddRange(ordered);
            }
            if (res.Windows.Count == 0) throw new ProviderException("Antigravity-Bericht enthält keine aktiven Kontingente.");

            UsageWindow a = gem5 ?? (gemW == null ? first5 : null);
            UsageWindow bw = gemW ?? (gem5 == null ? firstW : null);
            if (a == null) { a = bw; bw = null; }   // z.B. Starter-Konten nur mit Wochenlimit
            res.BarA = a ?? res.Windows[0];
            res.BarB = bw != res.BarA ? bw : null;
            res.Plan = "Antigravity";
            return res;
        }
    }

    // Job-Objekt: schließt man es, beendet Windows alle zugeordneten Prozesse (inkl. Kindprozesse von agy)
    static class Job
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BASIC
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct EXTENDED
        {
            public BASIC Basic;
            public IO Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref EXTENDED info, int len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        public static IntPtr Create()
        {
            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                EXTENDED info = new EXTENDED();
                info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(EXTENDED)));
                return job;
            }
            catch { return IntPtr.Zero; }
        }

        public static void Assign(IntPtr job, Process p)
        {
            if (job == IntPtr.Zero) return;
            try { AssignProcessToJobObject(job, p.Handle); } catch { }
        }

        public static void Close(IntPtr job)
        {
            if (job == IntPtr.Zero) return;
            try { CloseHandle(job); } catch { }
        }
    }
}
