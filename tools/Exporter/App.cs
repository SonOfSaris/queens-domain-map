using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QDExport;

/// <summary>
/// The map as a program. Started without a command it finds the game, builds the map data from the game's files when
/// there is none yet, serves the page to the browser on this PC only, and stays next to the clock until Quit.
/// </summary>
public static class App
{
    public const string Name = "Queen's Domain Map";
    public const string Id = "QueensDomainMap";

    /// <summary>Raise when the exporter writes something the page needs that older data lacks, or corrects what it wrote: the page then offers a rebuild.</summary>
    public const int DataFormat = 2;

    public static readonly string Version =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev";

    private const string Usage =
        "Queen's Domain Map\n\n" +
        "  QueensDomainMap.exe [options]              start the map and open it in the browser\n" +
        "    --port=<number>    serve on another port than 8765 (found marks are kept per port)\n" +
        "    --home=<folder>    where the map data and folders.json are kept (default: beside the program)\n" +
        "    --game=<folder>    the game folder, for this run only\n" +
        "    --saves=<folder>   the save folder, for this run only\n" +
        "    --no-open          do not open the browser\n" +
        "    --no-tray          no icon next to the clock (stop with Ctrl+C or from the page)\n" +
        "    --web=<folder>     serve the page's files from a folder instead of the built-in copy\n\n" +
        "  QueensDomainMap.exe <Paks folder> export <out folder>     build the map data by hand\n" +
        "  QueensDomainMap.exe <Paks folder> <command> ...           research commands, see the README";

    private static int _port = 8765;
    private static string _home, _webDir;
    private static string _forcedGame = "", _forcedSaves = "";
    private static bool _open = true, _tray = true;
    private static readonly ManualResetEventSlim Quit = new();
    private static string[] _hosts;

    private static string Url => $"http://localhost:{_port}/";
    private static string DataDir => Path.Combine(_home, "data");
    private static string FoldersFile => Path.Combine(_home, "folders.json");

    public static int Run(string[] args)
    {
        foreach (var a in args)
        {
            string V(string name) => a.StartsWith("--" + name + "=") ? a[(name.Length + 3)..] : null;
            if (a is "--help" or "-h" or "/?") { Win.AttachParentConsole(); Console.WriteLine(Usage); return 0; }
            if (a == "--version") { Win.AttachParentConsole(); Console.WriteLine($"{Id} {Version}"); return 0; }
            if (a == "--no-open") _open = false;
            else if (a == "--no-tray") _tray = false;
            else if (V("port") is { } p && int.TryParse(p, out var n) && n is > 0 and < 65536) _port = n;
            else if (V("home") is { } h) _home = CleanPath(h);
            else if (V("game") is { } g) _forcedGame = CleanPath(g);
            else if (V("saves") is { } s) _forcedSaves = CleanPath(s);
            else if (V("web") is { } w) _webDir = Trim(Path.GetFullPath(CleanPath(w)));
            else { Win.AttachParentConsole(); Win.Message($"Unknown option: {a}\n\n{Usage}", true); return 2; }
        }
        _home = ResolveHome(_home);
        _hosts = new[] { $"localhost:{_port}", $"127.0.0.1:{_port}" };

        // Already running? Then this start only brings the page up again.
        var taken = Probe();
        if (taken == "ours") { if (_open) OpenBrowser(""); return 0; }
        if (taken == "other")
        {
            Win.Message($"Port {_port} on this PC is already in use.\n\n" +
                        "If an older map window (Open Map.bat) is still open, close it and start the map again.\n" +
                        $"Otherwise start it on another port:  {Id}.exe --port={_port + 1}\n(found marks are kept per port)", true);
            return 1;
        }

        HttpListener listener;
        try { listener = Listen(); }
        catch (Exception e) { Win.Message($"The map could not start its local web server on port {_port}:\n{e.Message}", true); return 1; }
        _ = Task.Run(() => Serve(listener));

        var game = CurrentGame();
        Console.Error.WriteLine($"{Name} {Version}: {Url}");
        Console.Error.WriteLine($"Map data: {DataDir}");
        Console.Error.WriteLine($"Game: {(game != null ? game.Home : "not found")}");
        if (_open) OpenBrowser("");

        if (_tray && Win.Is)
        {
            new Thread(() => { Quit.Wait(); Tray.Close(); }) { IsBackground = true }.Start();
            Tray.Run(Name + " - running. Right-click to quit.",
                ("Open the map", () => OpenBrowser("")),
                ("Gear stats", () => OpenBrowser("stats.html")),
                ("-", null),
                ("Quit", Tray.Close));
            Quit.Set();
        }
        else
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Quit.Set(); };
            Console.Error.WriteLine("Press Ctrl+C to stop.");
            Quit.Wait();
        }
        Build.Stop();
        try { listener.Stop(); } catch { /* going down anyway */ }
        return 0;
    }

    // ------------------------------------------------------------------ start-up helpers

    /// <summary>The folder for the map data and settings: beside the program, or the user's app data when that cannot be written.</summary>
    private static string ResolveHome(string asked)
    {
        var home = Trim(Path.GetFullPath(string.IsNullOrEmpty(asked) ? AppContext.BaseDirectory : asked));
        try
        {
            Directory.CreateDirectory(home);
            var probe = Path.Combine(home, ".write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return home;
        }
        catch
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Id);
            Directory.CreateDirectory(local);
            return local;
        }
    }

    /// <summary>"free", "ours" (this program already serves the port) or "other".</summary>
    private static string Probe()
    {
        var answers = false;
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                using var c = new TcpClient(ip.AddressFamily);
                if (c.ConnectAsync(ip, _port).Wait(400) && c.Connected) answers = true;
            }
            catch { /* nobody there */ }
        }
        if (!answers) return "free";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var text = http.GetStringAsync(Url + "api/status").GetAwaiter().GetResult();
            if (JsonNode.Parse(text)?["app"]?.GetValue<string>() == Id) return "ours";
        }
        catch { /* not this program */ }
        return "other";
    }

    private static HttpListener Listen()
    {
        try
        {
            var both = new HttpListener { IgnoreWriteExceptions = true };
            foreach (var h in _hosts) both.Prefixes.Add($"http://{h}/");
            both.Start();
            return both;
        }
        catch (HttpListenerException)
        {
            // some systems only let a normal user listen on the name, not on the address
            var one = new HttpListener { IgnoreWriteExceptions = true };
            one.Prefixes.Add($"http://{_hosts[0]}/");
            one.Start();
            return one;
        }
    }

    private static void OpenBrowser(string page)
    {
        try { Process.Start(new ProcessStartInfo(Url + page) { UseShellExecute = true }); }
        catch (Exception e) { Console.Error.WriteLine("The browser could not be opened: " + e.Message); }
    }

    // ------------------------------------------------------------------ folders: where the game is and where it keeps its saves
    // Set on the map page (Save box > "Game and save folders…") and kept in folders.json; --game= / --saves= replace them
    // for one run. Left empty, the map looks by itself: around its own folder and in the Steam libraries for the game,
    // beside the game and in %LOCALAPPDATA%\QueensDomain for saves.

    public sealed record PakInfo(string Name, long Size, long Mtime);
    public sealed record Game(string Paks, string Pak, long Mtime, string Project, string Home, List<PakInfo> All);
    private sealed record SaveEntry(string Id, string FullPath, string Root, int Profile, string File, int Slot, bool Backup, long Mtime, long Size);

    private static string CleanPath(string v)
    {
        if (v == null) return "";
        v = v.Trim().Trim('"').Trim();          // "Copy as path" adds quotes
        return v.Length > 1024 ? v[..1024] : v;
    }

    private static string Trim(string full) => full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    private static string Full(params string[] parts) => Trim(Path.GetFullPath(Path.Combine(parts)));
    private static long Ms(DateTime utc) => (long) Math.Round((utc - DateTime.UnixEpoch).TotalMilliseconds, MidpointRounding.AwayFromZero);

    private static (string Game, string Saves) ReadFolders()
    {
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(FoldersFile));
            return (CleanPath(j?["gameFolder"]?.GetValue<string>()), CleanPath(j?["saveFolder"]?.GetValue<string>()));
        }
        catch { return ("", ""); }
    }

    /// <summary>The game in a folder: its install folder, the inner QueensDomain folder or the Paks folder itself.</summary>
    public static Game FindGame(string folder)
    {
        foreach (var rel in new[] { @"QueensDomain\Content\Paks", @"Content\Paks", "." })
        {
            try
            {
                var paks = Full(folder, rel);
                if (!Directory.Exists(paks)) continue;
                var all = new DirectoryInfo(paks).GetFiles("*.pak").Where(f => f.Extension.Equals(".pak", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => new PakInfo(f.Name, f.Length, Ms(f.LastWriteTimeUtc))).ToList();
                if (all.Count == 0) continue;
                var main = all.OrderByDescending(p => p.Size).First();     // the game's own pak, not a small mod pak beside it
                var project = Full(paks, "..", "..");
                return new Game(paks, main.Name, main.Mtime, project, Full(project, ".."), all);
            }
            catch { /* not there */ }
        }
        return null;
    }

    private static List<string> _steam;
    private static DateTime _steamAt;

    /// <summary>Where the game is looked for when no folder was named: around the map's own folder, then in Steam.</summary>
    private static Game AutoGame()
    {
        var places = new List<string> { Full(_home, "..") };
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (var up = 0; d != null && up < 4; d = d.Parent, up++) places.Add(d.FullName);
        lock (Quit)
        {
            if (_steam == null || DateTime.UtcNow - _steamAt > TimeSpan.FromSeconds(30)) { _steam = Win.SteamGameFolders(); _steamAt = DateTime.UtcNow; }
            places.AddRange(_steam);
        }
        foreach (var p in places.Distinct(StringComparer.OrdinalIgnoreCase))
            if (FindGame(p) is { } g) return g;
        return null;
    }

    private static Game GameFor(string set) => (_forcedGame.Length > 0 ? _forcedGame : set) is { Length: > 0 } asked ? FindGame(asked) : AutoGame();
    public static Game CurrentGame() => GameFor(ReadFolders().Game);

    /// <summary>Folders to look for saves in. A save folder that was named is the only place; it may also be the game's or its Saved folder.</summary>
    private static List<string> SaveRoots((string Game, string Saves) set, Game game)
    {
        var asked = _forcedSaves.Length > 0 ? _forcedSaves : set.Saves;
        var list = new List<string>();
        if (asked.Length > 0)
        {
            list.Add(asked);
            list.Add(Path.Combine(asked, "SaveGames"));
            list.Add(Path.Combine(asked, "Saved", "SaveGames"));
        }
        else
        {
            if (game != null) { list.Add(Path.Combine(game.Home, "Saved", "SaveGames")); list.Add(Path.Combine(game.Project, "Saved", "SaveGames")); }
            if (_forcedGame.Length == 0 && set.Game.Length == 0) list.Add(Path.Combine(_home, "..", "Saved", "SaveGames"));
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local)) list.Add(Path.Combine(local, "QueensDomain", "Saved", "SaveGames"));
        }
        var roots = new List<string>();
        foreach (var p in list)
        {
            string full;
            try { full = Full(p); } catch { continue; }
            if (!roots.Contains(full)) roots.Add(full);
        }
        return roots;
    }

    // ---- save games: the map reads the game's save slots (read-only) to tick off what you already have
    private static readonly Regex SaveRe = new(@"^World(\d+)(_backup(?:_\d+)?)?\.sav$", RegexOptions.IgnoreCase);

    private static List<SaveEntry> ListSaves(List<string> roots)
    {
        var list = new List<SaveEntry>();
        var seen = new HashSet<string>();
        var profile = 0; // folders are numbered rather than named: the game names them after the Steam account
        foreach (var dir in roots)
        {
            List<string> folders;
            try
            {
                folders = new List<string> { dir };
                folders.AddRange(Directory.GetDirectories(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).Select(n => Path.Combine(dir, n)));
            }
            catch { continue; }
            foreach (var folder in folders)
            {
                if (!seen.Add(folder.ToLowerInvariant())) continue;
                FileInfo[] files;
                try { files = new DirectoryInfo(folder).GetFiles(); } catch { continue; }
                var any = false;
                foreach (var f in files)
                {
                    var m = SaveRe.Match(f.Name);
                    if (!m.Success) continue;
                    try
                    {
                        var full = Path.Combine(folder, f.Name);
                        var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()))).ToLowerInvariant()[..12];
                        list.Add(new SaveEntry(id, full, dir, profile, f.Name, int.Parse(m.Groups[1].Value), m.Groups[2].Success, Ms(f.LastWriteTimeUtc), f.Length));
                        any = true;
                    }
                    catch { /* vanished while the game was writing it */ }
                }
                if (any) profile++;
            }
        }
        return list;
    }

    private static List<SaveEntry> CurrentSaves()
    {
        var set = ReadFolders();
        return ListSaves(SaveRoots(set, GameFor(set.Game)));
    }

    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode) JsonValue.Create(s)).ToArray());

    /// <summary>The answer of api/folders: what is set, what it leads to, and what is wrong with it.</summary>
    private static JsonObject FolderState((string Game, string Saves) set)
    {
        var asked = _forcedGame.Length > 0 ? _forcedGame : set.Game;
        var game = GameFor(set.Game);
        var roots = SaveRoots(set, game);
        var list = ListSaves(roots);

        var g = new JsonObject { ["set"] = set.Game, ["forced"] = _forcedGame.Length > 0 ? _forcedGame : null, ["found"] = game != null };
        if (game != null) { g["home"] = game.Home; g["pak"] = game.Pak; g["mtime"] = game.Mtime; }
        else if (asked.Length > 0) g["error"] = Directory.Exists(asked) ? "The game is not in that folder. Pick the one that holds QueensDomain.exe." : "That folder does not exist.";

        var s = new JsonObject
        {
            ["set"] = set.Saves, ["forced"] = _forcedSaves.Length > 0 ? _forcedSaves : null,
            ["slots"] = list.Count(x => !x.Backup), ["backups"] = list.Count(x => x.Backup),
            ["dirs"] = Strings(list.Select(x => x.Root).Distinct()), ["looked"] = Strings(roots),
        };
        var askedSaves = _forcedSaves.Length > 0 ? _forcedSaves : set.Saves;
        if (askedSaves.Length > 0 && !Directory.Exists(askedSaves)) s["error"] = "That folder does not exist.";
        return new JsonObject { ["game"] = g, ["saves"] = s, ["canBrowse"] = Win.Is };
    }

    private static readonly Dictionary<string, string> PickTitles = new()
    {
        ["game"] = "Queen's Domain: select the game folder (the one with QueensDomain.exe)",
        ["saves"] = "Queen's Domain: select the save folder (the one with World0.sav)",
    };

    // ------------------------------------------------------------------ map data: is it there, and does it still match the game

    private static JsonObject DataState(Game game)
    {
        var ready = File.Exists(Path.Combine(DataDir, "world.json")) && File.Exists(Path.Combine(DataDir, "markers.json"));
        var o = new JsonObject { ["ready"] = ready, ["built"] = null, ["stale"] = "" };
        if (!ready) return o;
        try
        {
            var info = JsonNode.Parse(File.ReadAllText(Path.Combine(DataDir, "build.json")));
            o["built"] = info["built"]?.GetValue<long>();
            if ((info["format"]?.GetValue<int>() ?? 0) < DataFormat) o["stale"] = "version";
            else if (game != null)
            {
                var then = (info["paks"] as JsonArray ?? new JsonArray()).Select(p => $"{p["name"]}|{p["size"]}|{p["mtime"]}");
                var now = game.All.Select(p => $"{p.Name}|{p.Size}|{p.Mtime}");
                if (!then.SequenceEqual(now)) o["stale"] = "game";
            }
        }
        catch { /* data from before the program kept a record: taken as it is */ }
        return o;
    }

    /// <summary>The build of the map data: this program again, as a second process that reads the game and writes the files.</summary>
    private static class Build
    {
        private static readonly object Gate = new();
        private static string _state = "idle", _text = "", _error;
        private static double _fraction;
        private static DateTime _started, _ended;
        private static readonly List<string> Log = new();
        private static Process _process;

        public static JsonObject State()
        {
            lock (Gate)
            {
                var until = _state == "running" ? DateTime.UtcNow : _ended;
                return new JsonObject
                {
                    ["state"] = _state, ["fraction"] = Math.Round(_fraction, 3), ["text"] = _text,
                    ["seconds"] = _state == "idle" ? 0 : (int) (until - _started).TotalSeconds,
                    ["error"] = _error, ["log"] = Strings(Log.TakeLast(12)),
                };
            }
        }

        /// <summary>Null when the build was started, else why not.</summary>
        public static string Start(Game game)
        {
            lock (Gate)
            {
                if (_state == "running") return "The map is being built already.";
                if (game == null) return "The game was not found. Set the game folder first.";
                try
                {
                    var free = new DriveInfo(Path.GetPathRoot(_home)).AvailableFreeSpace;
                    if (free < 900L << 20) return $"Not enough free disk space in {_home}: the map data takes about 600 MB, {free >> 20} MB are free.";
                }
                catch { /* a network folder or the like: try anyway */ }
                _state = "running"; _text = "Starting"; _fraction = 0; _error = null; _started = DateTime.UtcNow;
                Log.Clear();
            }
            _ = Task.Run(() => Work(game));
            return null;
        }

        public static void Stop()
        {
            try { _process?.Kill(true); } catch { /* gone already */ }
        }

        private static void Fail(string why)
        {
            lock (Gate) { _state = "failed"; _error = why; _ended = DateTime.UtcNow; }
        }

        private static void Work(Game game)
        {
            var fresh = DataDir + ".new";
            try
            {
                if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
                var exe = Environment.ProcessPath;
                var start = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
                };
                // started as "dotnet QDExport.dll" during development
                if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(App).Assembly.Location);
                start.ArgumentList.Add(game.Paks);
                start.ArgumentList.Add("export");
                start.ArgumentList.Add(fresh);
                start.Environment["QDMAP_CHILD"] = "1";

                using var p = Process.Start(start);
                _process = p;
                p.OutputDataReceived += (_, _) => { };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) Line(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                _process = null;
                if (Quit.IsSet) return;
                if (p.ExitCode != 0 || !File.Exists(Path.Combine(fresh, "world.json")) || !File.Exists(Path.Combine(fresh, "markers.json")))
                {
                    string last;
                    lock (Gate) last = string.Join("\n", Log.TakeLast(6));
                    Fail($"The export stopped with an error (code {p.ExitCode}).\n{last}");
                    return;
                }

                var info = new JsonObject
                {
                    ["format"] = DataFormat, ["app"] = Version, ["built"] = Ms(DateTime.UtcNow),
                    ["paks"] = new JsonArray(game.All.Select(k => (JsonNode) new JsonObject { ["name"] = k.Name, ["size"] = k.Size, ["mtime"] = k.Mtime }).ToArray()),
                };
                File.WriteAllText(Path.Combine(fresh, "build.json"), info.ToJsonString(JsonOut) + "\n");

                lock (Gate) { _text = "Putting the new map data in place"; _fraction = 0.99; }
                var old = DataDir + ".old";
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(fresh))
                        {
                            if (Directory.Exists(old)) Directory.Delete(old, true);
                            if (Directory.Exists(DataDir)) Directory.Move(DataDir, old);
                            Directory.Move(fresh, DataDir);
                        }
                        break;
                    }
                    catch (Exception) when (attempt < 20) { Thread.Sleep(500); } // a browser tab is still reading the old files
                }
                try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { /* removed by the next build */ }
                lock (Gate) { _state = "done"; _text = "Done"; _fraction = 1; _ended = DateTime.UtcNow; }
            }
            catch (Exception e)
            {
                _process = null;
                Fail(e.Message);
            }
        }

        private static void Line(string line)
        {
            lock (Gate)
            {
                // "@@ 0.42 Zone 12 of 39": how far the export is
                if (line.StartsWith("@@ "))
                {
                    var parts = line.Split(' ', 3);
                    if (parts.Length == 3 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                    {
                        _fraction = Math.Clamp(f, 0, 0.98);
                        _text = parts[2];
                    }
                    return;
                }
                Log.Add(line.Length > 400 ? line[..400] : line);
                if (Log.Count > 400) Log.RemoveRange(0, 200);
            }
        }
    }

    // ------------------------------------------------------------------ web server

    private static readonly JsonSerializerOptions JsonOut = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8", [".png"] = "image/png", [".jpg"] = "image/jpeg",
        [".bin"] = "application/octet-stream", [".ico"] = "image/x-icon", [".svg"] = "image/svg+xml", [".md"] = "text/plain; charset=utf-8",
        [".tsv"] = "text/plain; charset=utf-8", [".txt"] = "text/plain; charset=utf-8",
    };

    private static async Task Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(async () =>
            {
                try { await Handle(ctx); }
                catch (Exception e)
                {
                    try { ctx.Response.StatusCode = 500; await Text(ctx, e.Message); } catch { /* the answer had started already, or the browser left */ }
                }
                finally { try { ctx.Response.Close(); } catch { /* the browser left */ } }
            });
        }
    }

    private static async Task Text(HttpListenerContext ctx, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task Json(HttpListenerContext ctx, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString(JsonOut));
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static Task Deny(HttpListenerContext ctx, int code, string text)
    {
        ctx.Response.StatusCode = code;
        return Text(ctx, text);
    }

    private static async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        // This PC only. Windows may accept connections for this port on other network addresses too; they end here.
        if (req.RemoteEndPoint == null || !IPAddress.IsLoopback(req.RemoteEndPoint.Address)) { await Deny(ctx, 403, "Forbidden"); return; }
        string rel;
        try { rel = Uri.UnescapeDataString(req.Url.AbsolutePath); }
        catch { ctx.Response.StatusCode = 400; return; }

        if (rel.StartsWith("/api/")) { await Api(ctx, rel); return; }
        if (req.HttpMethod != "GET" && req.HttpMethod != "HEAD") { await Deny(ctx, 405, "Method not allowed"); return; }
        if (rel.EndsWith('/')) rel += "index.html";

        // the map data lies in a folder; the page itself is part of the program (or comes from --web)
        var fromData = rel.StartsWith("/data/");
        if (fromData || _webDir != null)
        {
            var root = fromData ? _home : _webDir;
            string file;
            try { file = Path.GetFullPath(Path.Combine(root, rel.TrimStart('/'))); }
            catch { await Deny(ctx, 400, "Bad path"); return; }
            if (!file.StartsWith((fromData ? DataDir : root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { await Deny(ctx, 403, "Forbidden"); return; }
            var info = new FileInfo(file);
            if (info.Exists)
            {
                if (!Fresh(ctx, info.LastWriteTimeUtc, info.Extension, info.Length)) return;
                await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await fs.CopyToAsync(ctx.Response.OutputStream);
                return;
            }
            if (fromData) { await Deny(ctx, 404, "Not found"); return; }
            // not in the --web folder: the built-in copy has it (the icon, for one)
        }

        await using var res = typeof(App).Assembly.GetManifestResourceStream("web" + rel);
        if (res == null) { await Deny(ctx, 404, "Not found"); return; }
        if (!Fresh(ctx, Started, Path.GetExtension(rel), res.Length)) return;
        await res.CopyToAsync(ctx.Response.OutputStream);
    }

    private static readonly DateTime Started = DateTime.UtcNow;

    /// <summary>Headers of a file answer. False when nothing more is to be sent (not modified, or only the headers were asked for).</summary>
    private static bool Fresh(HttpListenerContext ctx, DateTime modifiedUtc, string ext, long length)
    {
        // always revalidate, so that rebuilt data is picked up on the next reload
        var modified = modifiedUtc.ToString("R");
        ctx.Response.Headers["Last-Modified"] = modified;
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        if (ctx.Request.Headers["If-Modified-Since"] == modified) { ctx.Response.StatusCode = 304; return false; }
        ctx.Response.ContentType = Types.GetValueOrDefault(ext, "application/octet-stream");
        ctx.Response.ContentLength64 = length;
        return ctx.Request.HttpMethod != "HEAD";
    }

    private static async Task<JsonNode> ReadBody(HttpListenerRequest req)
    {
        var buffer = new byte[8193];
        var n = 0;
        while (n < buffer.Length)
        {
            var got = await req.InputStream.ReadAsync(buffer.AsMemory(n));
            if (got == 0) break;
            n += got;
        }
        if (n > 8192) throw new InvalidOperationException("Too large");
        return (n == 0 ? null : JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, n))) ?? new JsonObject();
    }

    private static string Str(JsonNode body, string key) => body?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Flag(JsonNode body, string key) => body?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static async Task Api(HttpListenerContext ctx, string rel)
    {
        var req = ctx.Request;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        if (!_hosts.Contains(req.Headers["Host"])) { await Deny(ctx, 403, "Forbidden"); return; } // the map page itself only, not another site's name for this PC
        var post = req.HttpMethod == "POST";
        if (post)
        {
            // Changing something takes a JSON request from the map page: another web site cannot send one here.
            var origin = req.Headers["Origin"];
            if ((!string.IsNullOrEmpty(origin) && !_hosts.Any(h => origin == "http://" + h)) || !Regex.IsMatch(req.ContentType ?? "", @"^application/json\b", RegexOptions.IgnoreCase))
            {
                await Deny(ctx, 403, "Forbidden");
                return;
            }
        }
        else if (req.HttpMethod != "GET") { await Deny(ctx, 405, "Method not allowed"); return; }

        switch (rel, post)
        {
            case ("/api/saves", false):
            {
                var saves = CurrentSaves().Select(s => (JsonNode) new JsonObject
                {
                    ["id"] = s.Id, ["profile"] = s.Profile, ["file"] = s.File, ["slot"] = s.Slot, ["backup"] = s.Backup, ["mtime"] = s.Mtime, ["size"] = s.Size,
                });
                await Json(ctx, new JsonObject { ["saves"] = new JsonArray(saves.ToArray()) });
                break;
            }
            case ("/api/save", false):
            {
                // only files that the listing itself produced can be read
                var hit = CurrentSaves().FirstOrDefault(s => s.Id == req.QueryString["id"]);
                if (hit == null) { await Deny(ctx, 404, "No such save"); return; }
                byte[] data;
                try
                {
                    await using var fs = new FileStream(hit.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    data = new byte[fs.Length];
                    await fs.ReadExactlyAsync(data);
                }
                catch { await Deny(ctx, 503, "Save is being written"); return; }
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
                break;
            }
            case ("/api/folders", false):
                await Json(ctx, FolderState(ReadFolders()));
                break;
            case ("/api/folders", true):
            {
                // { game, saves }: store them, unless one of them is wrong. With check: true, only say what they would lead to.
                var body = await ReadBody(req);
                var set = (Game: CleanPath(Str(body, "game")), Saves: CleanPath(Str(body, "saves")));
                var state = FolderState(set);
                if (!Flag(body, "check"))
                {
                    var saved = state["game"]["error"] == null && state["saves"]["error"] == null;
                    state["saved"] = saved;
                    if (saved)
                    {
                        var json = new JsonObject { ["gameFolder"] = set.Game, ["saveFolder"] = set.Saves }.ToJsonString(new JsonSerializerOptions(JsonOut) { WriteIndented = true });
                        File.WriteAllText(FoldersFile, json.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
                    }
                }
                await Json(ctx, state);
                break;
            }
            case ("/api/browse", true):
            {
                var body = await ReadBody(req);
                var which = Str(body, "which") ?? "";
                var path = await Win.PickFolder(PickTitles.GetValueOrDefault(which, PickTitles["game"]), CleanPath(Str(body, "start")));
                await Json(ctx, new JsonObject { ["path"] = path });
                break;
            }
            case ("/api/status", false):
            {
                var game = CurrentGame();
                var g = new JsonObject { ["found"] = game != null };
                if (game != null) { g["home"] = game.Home; g["pak"] = game.Pak; g["mtime"] = game.Mtime; }
                await Json(ctx, new JsonObject
                {
                    ["app"] = Id, ["version"] = Version, ["tray"] = Tray.Shown,
                    ["data"] = DataState(game), ["game"] = g, ["build"] = Build.State(),
                });
                break;
            }
            case ("/api/build", true):
            {
                var why = Build.Start(CurrentGame());
                await Json(ctx, new JsonObject { ["started"] = why == null, ["error"] = why, ["build"] = Build.State() });
                break;
            }
            case ("/api/quit", true):
                await Json(ctx, new JsonObject { ["quit"] = true });
                _ = Task.Run(async () => { await Task.Delay(300); Quit.Set(); });
                break;
            default:
                await Deny(ctx, 404, "Not found");
                break;
        }
    }
}
