// AI Usage Monitor - eigene Tray-Symbole über Shell_NotifyIcon mit fester GUID je Symbol.
// Mit GUID kann Windows die Symbole eindeutig unterscheiden und sich ihre Position merken
// (der .NET-Baustein NotifyIcon setzt keine GUID).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("AI Usage Monitor")]
[assembly: AssemblyProduct("AI Usage Monitor")]
[assembly: AssemblyDescription("Zeigt die verbleibenden Limits von Claude, ChatGPT, Gemini und GitHub Copilot")]
[assembly: AssemblyVersion("1.1.5.0")]
[assembly: AssemblyFileVersion("1.1.5.0")]

namespace AIUsageMonitor
{
    public class TrayIcon : IDisposable
    {
        // ---------------------------------------------------------------- Win32
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
        const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_GUID = 0x20;
        const int WM_APP_TRAY = 0x8000 + 77;
        const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;

        // ---------------------------------------------------------------- gemeinsames, verstecktes Fenster
        class TrayWindow : NativeWindow
        {
            public readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
            public TrayWindow() { CreateHandle(new CreateParams()); }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_APP_TRAY)
                {
                    int id = (int)(long)m.WParam;
                    int evt = (int)((long)m.LParam & 0xFFFF);
                    TrayIcon icon;
                    if (All.TryGetValue(id, out icon))
                    {
                        if (evt == WM_LBUTTONUP && icon.LeftClick != null) icon.LeftClick(icon, EventArgs.Empty);
                        else if (evt == WM_RBUTTONUP && icon.RightClick != null) icon.RightClick(icon, EventArgs.Empty);
                    }
                    return;
                }
                if (m.Msg == TaskbarCreated && TaskbarCreated != 0)
                {
                    // Explorer wurde neu gestartet -> alle Symbole neu anlegen
                    foreach (TrayIcon t in new List<TrayIcon>(All.Values)) { t.Reset(); t.Push(); }
                }
                base.WndProc(ref m);
            }
        }

        static TrayWindow window;
        static readonly Dictionary<int, TrayIcon> All = new Dictionary<int, TrayIcon>();
        static int nextId = 1;

        public static IntPtr WindowHandle { get { EnsureWindow(); return window.Handle; } }

        static void EnsureWindow() { if (window == null) window = new TrayWindow(); }

        // ---------------------------------------------------------------- Instanz
        public event EventHandler LeftClick;
        public event EventHandler RightClick;

        readonly int id;
        readonly Guid guid;
        IntPtr hIcon;
        string tip = "";
        bool added;
        bool visible;
        int guidFailures;

        public TrayIcon(string key)
        {
            EnsureWindow();
            id = nextId++;
            guid = StableGuid(key);
            All[id] = this;
        }

        // Windows bindet eine Tray-GUID an den Pfad der EXE. Deshalb wird die GUID aus Pfad + Schlüssel
        // abgeleitet: gleich bleibend für dieselbe EXE, neu (statt Fehler), falls der Ordner verschoben wird.
        static Guid StableGuid(string key)
        {
            string src = Application.ExecutablePath.ToLowerInvariant() + "|AIUsageMonitor|" + key;
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(src));
                h[6] = (byte)((h[6] & 0x0F) | 0x30); // Version 3 (namensbasiert)
                h[8] = (byte)((h[8] & 0x3F) | 0x80);
                return new Guid(h);
            }
        }

        NOTIFYICONDATA Data(int flags, bool useGuid)
        {
            NOTIFYICONDATA d = new NOTIFYICONDATA();
            d.cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
            d.hWnd = window.Handle;
            d.uID = id;
            d.uFlags = useGuid ? (flags | NIF_GUID) : flags;
            if (useGuid) d.guidItem = guid;
            d.uCallbackMessage = WM_APP_TRAY;
            d.hIcon = hIcon;
            d.szTip = tip.Length > 127 ? tip.Substring(0, 127) : tip;
            return d;
        }

        public string Tip { get { return tip; } }

        public void Update(IntPtr icon, string tooltip)
        {
            hIcon = icon;
            tip = tooltip ?? "";
            Push();
        }

        public bool Visible
        {
            get { return visible; }
            set
            {
                visible = value;
                if (value) Push();
                else Remove();
            }
        }

        // Explorer wurde neu gestartet: Symbol muss neu angelegt werden
        internal void Reset() { added = false; guidFailures = 0; }

        // Regelmäßig aufrufen: legt ein fehlendes Symbol neu an (z.B. wenn Explorer beim Neustart noch nicht bereit war)
        public void Refresh() { Push(); }

        void Push()
        {
            if (!visible || hIcon == IntPtr.Zero) return;
            bool useGuid = guidFailures < 3;
            NOTIFYICONDATA d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP, useGuid);
            if (added && Shell_NotifyIcon(NIM_MODIFY, ref d)) return;

            // Alle denkbaren Einträge dieses Symbols entfernen (mit und ohne GUID), damit nie ein
            // veraltetes Doppel stehen bleibt, das keine Aktualisierungen mehr bekommt
            DeleteAll();
            added = Shell_NotifyIcon(NIM_ADD, ref d);
            if (added) { if (useGuid) guidFailures = 0; return; }

            if (useGuid)
            {
                guidFailures++;
                Log.Write("Tray-Symbol konnte nicht angelegt werden (Versuch " + guidFailures + ", wird wiederholt)");
                if (guidFailures >= 3)
                {
                    // Notlösung nach mehreren Fehlversuchen: ohne GUID (Position wird dann evtl. nicht gemerkt)
                    d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP, false);
                    added = Shell_NotifyIcon(NIM_ADD, ref d);
                    if (added) Log.Write("Tray-Symbol ohne GUID angelegt");
                }
            }
        }

        void DeleteAll()
        {
            NOTIFYICONDATA a = Data(0, true);
            Shell_NotifyIcon(NIM_DELETE, ref a);
            NOTIFYICONDATA b = Data(0, false);
            Shell_NotifyIcon(NIM_DELETE, ref b);
        }

        void Remove()
        {
            DeleteAll();
            added = false;
        }

        // Position des Symbols auf dem Bildschirm (für die Reihenfolge in der Detailansicht)
        [StructLayout(LayoutKind.Sequential)]
        struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public int uID; public Guid guidItem; }
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("shell32.dll")]
        static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        public bool TryGetRect(out System.Drawing.Rectangle rect)
        {
            rect = System.Drawing.Rectangle.Empty;
            if (!added) return false;
            try
            {
                NOTIFYICONIDENTIFIER nid = new NOTIFYICONIDENTIFIER();
                nid.cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
                nid.hWnd = window.Handle;
                nid.uID = id;
                if (guidFailures < 3) nid.guidItem = guid;
                RECT r;
                if (Shell_NotifyIconGetRect(ref nid, out r) != 0) return false;
                rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                return rect.Width > 0 && rect.Height > 0;
            }
            catch { return false; }
        }

        public static void ShowMenu(ContextMenuStrip menu)
        {
            // Ohne Vordergrund-Fenster schließt sich das Menü beim Wegklicken nicht
            SetForegroundWindow(WindowHandle);
            menu.Show(Cursor.Position);
        }

        public void Dispose()
        {
            visible = false;
            Remove();
            All.Remove(id);
        }
    }

    // Ermittelt, wo die eigenen Tray-Symbole in der Taskleiste stehen, über UI Automation (COM, "UIA3"):
    // Windows 11 zeigt jedes Tray-Symbol als Element, dessen Name der Tooltip ist.
    // (Die .NET-Bibliothek System.Windows.Automation sieht die XAML-Taskleiste von Windows 11 nicht.)
    public static class TaskbarOrder
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string title);

        [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomation
        {
            void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
            [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElement ElementFromHandle(IntPtr hwnd);
            void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
            void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
            void CreateTreeWalker(); void get_ControlViewWalker(); void get_ContentViewWalker(); void get_RawViewWalker();
            void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition(); void CreateCacheRequest();
            [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationCondition CreateTrueCondition();
        }

        [ComImport, Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomationCondition { }

        [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomationElement
        {
            void SetFocus(); void GetRuntimeId(); void FindFirst();
            [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElementArray FindAll(int scope, [MarshalAs(UnmanagedType.Interface)] IUIAutomationCondition condition);
            void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
            [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
        }

        [ComImport, Guid("14314595-B4BC-4055-95F2-58F2E42C9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomationElementArray
        {
            int get_Length();
            [return: MarshalAs(UnmanagedType.Interface)] IUIAutomationElement GetElement(int index);
        }

        const int UIA_BoundingRectanglePropertyId = 30001, UIA_NamePropertyId = 30005, TreeScope_Descendants = 4;
        static IUIAutomation uia;
        static bool unavailable;
        static string lastDiag;

        static bool Matches(string name, string tip)
        {
            // Windows 11 setzt den Namen aus dem ersten und dem aktuellen Tooltip zusammen
            // (z.B. "Claude: lädt… Claude frei: 5h 60 % · Woche 82 %"), daher "enthält".
            // Der Tooltip ist lang genug, dass z.B. das Tray-Symbol der Claude-App ("Claude") nicht passt.
            return name.IndexOf(tip, StringComparison.Ordinal) >= 0;
        }

        // Schlüssel: Anbieter-Id, Wert: Mittelpunkt des Symbols in Bildschirmkoordinaten
        public static Dictionary<string, System.Drawing.PointF> Find(Dictionary<string, string> tipById)
        {
            Dictionary<string, System.Drawing.PointF> res = new Dictionary<string, System.Drawing.PointF>();
            if (unavailable) return res;
            List<string> candidates = new List<string>();
            int count = 0;
            try
            {
                if (uia == null)
                    uia = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")));
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return res;
                IUIAutomationElement root = uia.ElementFromHandle(tray);
                IUIAutomationElementArray arr = root.FindAll(TreeScope_Descendants, uia.CreateTrueCondition());
                count = arr.get_Length();
                for (int i = 0; i < count; i++)
                {
                    IUIAutomationElement el = arr.GetElement(i);
                    string name = el.GetCurrentPropertyValue(UIA_NamePropertyId) as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    name = name.Trim();
                    if (name.IndexOf(" frei", StringComparison.Ordinal) >= 0 || name.IndexOf("lädt", StringComparison.Ordinal) >= 0
                        || name.IndexOf("eingerichtet", StringComparison.Ordinal) >= 0)
                        candidates.Add(name);
                    foreach (KeyValuePair<string, string> kv in tipById)
                    {
                        if (res.ContainsKey(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                        if (!Matches(name, kv.Value.Trim())) continue;
                        double[] r = el.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) as double[];
                        if (r == null || r.Length < 4 || r[2] <= 0 || r[3] <= 0) continue;
                        res[kv.Key] = new System.Drawing.PointF((float)(r[0] + r[2] / 2), (float)(r[1] + r[3] / 2));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Taskleisten-Reihenfolge per UI Automation nicht möglich: " + ex.Message);
                if (ex is InvalidCastException) unavailable = true;
            }
            // Diagnose (nur bei Änderung): wie viele Elemente, welche sahen aus wie unsere Symbole
            string diag = "UIA: " + count + " Elemente, " + res.Count + " zugeordnet" +
                (res.Count < tipById.Count && candidates.Count > 0 ? "; Kandidaten: " + string.Join(" | ", candidates.ToArray()) : "");
            if (diag != lastDiag) { Log.Write(diag); lastDiag = diag; }
            return res;
        }
    }
}
