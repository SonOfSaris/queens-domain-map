using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QDExport;

/// <summary>The Windows-only parts of the app: tray icon, folder window, message box, where Steam keeps its games.</summary>
public static class Win
{
    public static bool Is => OperatingSystem.IsWindows();

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    /// <summary>
    /// The released program has no console window of its own. Started from a command prompt with a command
    /// (QueensDomainMap.exe &lt;Paks&gt; export ...), it writes to that prompt instead.
    /// </summary>
    public static void AttachParentConsole()
    {
        if (Is && NoOutput) AttachConsole(-1);
    }

    /// <summary>True when nothing can be printed: started by double-click, without a console or a pipe.</summary>
    private static bool NoOutput => GetStdHandle(-12) is var err && (err == IntPtr.Zero || err == new IntPtr(-1));

    /// <summary>Something the user must see before the program ends: printed, or shown as a window when there is no console.</summary>
    public static void Message(string text, bool error = false)
    {
        if (Is && NoOutput) MessageBoxW(IntPtr.Zero, text, App.Name, error ? 0x10u : 0x40u);
        else Console.Error.WriteLine(text);
    }

    // ---- Steam: the game's folder in any of the Steam libraries on this PC

    private const string SteamAppId = "1799840";

    public static List<string> SteamGameFolders()
    {
        var found = new List<string>();
        if (!Is) return found;
        try
        {
            var steam = new List<string>();
            void Add(RegistryKey root, string key, string value)
            {
                try { if (root.OpenSubKey(key)?.GetValue(value) is string s && s.Length > 0) steam.Add(s.Replace('/', '\\')); }
                catch { /* not there */ }
            }
            Add(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            Add(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            Add(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

            var libraries = new List<string>(steam);
            foreach (var s in steam)
            {
                var vdf = Path.Combine(s, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (var line in File.ReadLines(vdf))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, "^\\s*\"path\"\\s+\"(.*)\"\\s*$");
                    if (m.Success) libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var apps = Path.Combine(lib, "steamapps");
                var dir = "QueensDomain";
                var manifest = Path.Combine(apps, $"appmanifest_{SteamAppId}.acf");
                if (File.Exists(manifest))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"(.*?)\"");
                    if (m.Success) dir = m.Groups[1].Value.Replace(@"\\", @"\");
                }
                var game = Path.Combine(apps, "common", dir);
                if (Directory.Exists(game)) found.Add(game);
            }
        }
        catch { /* no Steam, or its files are not readable: the folder can still be named by hand */ }
        return found;
    }

    // ---- the "Select Folder" window behind the Browse buttons on the page (a web page cannot ask for a folder's path)

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // Methods without parameters are never called: they only keep the others at their place in the interface.
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(); void SetFileTypeIndex(); void GetFileTypeIndex(); void Advise(); void Unadvise();
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder();
        void SetFolder(IShellItem folder);
        void GetFolder(); void GetCurrentSelection(); void SetFileName(); void GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel(); void SetFileNameLabel();
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(); void GetParent();
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItem item);

    private static int _picking;

    /// <summary>Shows the folder window and waits for it. Null when it was cancelled.</summary>
    public static Task<string> PickFolder(string title, string start)
    {
        if (!Is) throw new InvalidOperationException("No folder window on this system");
        if (Interlocked.Exchange(ref _picking, 1) == 1) throw new InvalidOperationException("A folder window is already open");
        var done = new TaskCompletionSource<string>();
        var thread = new Thread(() =>
        {
            var owner = IntPtr.Zero;
            try
            {
                // an owner that stays on top keeps the window in front of the browser
                const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 8, WS_EX_TOOLWINDOW = 0x80;
                owner = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "STATIC", App.Name, WS_POPUP,
                    GetSystemMetrics(0) / 2, GetSystemMetrics(1) / 2, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
                SetForegroundWindow(owner);
                var dialog = (IFileDialog) new FileOpenDialog();
                dialog.GetOptions(out var options);
                dialog.SetOptions(options | 0x20 | 0x40);   // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(start))
                {
                    try
                    {
                        SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out var folder);
                        dialog.SetFolder(folder);
                    }
                    catch { /* no such folder: the window opens where it last was */ }
                }
                if (dialog.Show(owner) != 0) { done.TrySetResult(null); return; }   // cancelled
                dialog.GetResult(out var result);
                result.GetDisplayName(0x80058000, out var path);                   // SIGDN_FILESYSPATH
                done.TrySetResult(path);
            }
            catch (Exception e) { done.TrySetException(e); }
            finally
            {
                if (owner != IntPtr.Zero) DestroyWindow(owner);
                Interlocked.Exchange(ref _picking, 0);
            }
        }) { IsBackground = true, Name = "folder window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}

/// <summary>The icon next to the clock: the program's only visible part besides the browser page.</summary>
public static class Tray
{
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);

    private const uint WM_DESTROY = 2, WM_CLOSE = 0x10, WM_NULL = 0, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONUP = 0x205, WM_TRAY = 0x8001;
    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    public const string WindowClass = "QueensDomainMapTray";

    private static WndProc _proc;       // kept alive for as long as the window exists
    private static NOTIFYICONDATA _icon;
    private static IntPtr _hwnd;
    private static uint _taskbarCreated;
    private static (string Text, Action Run)[] _items;

    /// <summary>True while the icon is really there (Windows can refuse it, for example when Explorer is not running).</summary>
    public static bool Shown { get; private set; }

    /// <summary>Puts the icon up and stays until Quit is chosen (or <see cref="Close"/> is called). The first item is what a click does.</summary>
    public static void Run(string tip, params (string Text, Action Run)[] items)
    {
        _items = items;
        var instance = GetModuleHandleW(null);
        _proc = Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint) Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance, lpszClassName = WindowClass,
        };
        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0, WindowClass, App.Name, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        var small = new IntPtr[1];
        var exe = Environment.ProcessPath;
        if (exe == null || ExtractIconExW(exe, 0, null, small, 1) == 0 || small[0] == IntPtr.Zero) small[0] = LoadIconW(IntPtr.Zero, new IntPtr(32512));
        _icon = new NOTIFYICONDATA
        {
            cbSize = (uint) Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY, hIcon = small[0], szTip = tip,
        };
        Shown = Shell_NotifyIconW(NIM_ADD, ref _icon);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Shell_NotifyIconW(NIM_DELETE, ref _icon);
        Shown = false;
    }

    /// <summary>Ends <see cref="Run"/> from any thread.</summary>
    public static void Close()
    {
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAY)
            {
                var what = (uint) (lParam.ToInt64() & 0xFFFF);
                if (what == WM_LBUTTONUP || what == WM_LBUTTONDBLCLK) _items[0].Run();
                else if (what == WM_RBUTTONUP) Menu(hWnd);
                return IntPtr.Zero;
            }
            if (msg == WM_CLOSE) { DestroyWindow(hWnd); return IntPtr.Zero; }
            if (msg == WM_DESTROY) { PostQuitMessage(0); return IntPtr.Zero; }
            if (msg == _taskbarCreated && _taskbarCreated != 0) Shown = Shell_NotifyIconW(NIM_ADD, ref _icon); // Explorer was restarted
        }
        catch { /* a failing menu action must not take the message loop down */ }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void Menu(IntPtr hWnd)
    {
        const uint MF_STRING = 0, MF_SEPARATOR = 0x800, TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
        var menu = CreatePopupMenu();
        for (var i = 0; i < _items.Length; i++)
        {
            if (_items[i].Text == "-") AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            else AppendMenuW(menu, MF_STRING, (UIntPtr) (uint) (i + 1), _items[i].Text);
        }
        SetMenuDefaultItem(menu, 1, 0);
        GetCursorPos(out var p);
        SetForegroundWindow(hWnd);          // or the menu stays open when you click elsewhere
        var id = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, p.X, p.Y, 0, hWnd, IntPtr.Zero);
        PostMessageW(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (id > 0 && id <= _items.Length) _items[id - 1].Run();
    }
}
