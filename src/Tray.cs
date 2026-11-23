// AI Usage Monitor - eigene Tray-Symbole über Shell_NotifyIcon mit fester GUID je Symbol.
// Mit GUID kann Windows die Symbole eindeutig unterscheiden und sich ihre Position merken
// (der .NET-Baustein NotifyIcon setzt keine GUID).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("AI Usage Monitor")]
[assembly: AssemblyProduct("AI Usage Monitor")]
[assembly: AssemblyDescription("Zeigt die verbleibenden Limits von Claude, ChatGPT, Gemini und GitHub Copilot")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

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
}
