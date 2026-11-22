using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;

namespace QDExport;

public static class Program
{
    public static CachingProvider Provider;

    public static int Main(string[] args)
    {
        // Without a command this is the map itself (web server, first-run build, tray icon): see App.cs.
        if (args.Length == 0 || args[0].StartsWith("--") || args[0] is "-h" or "/?") return App.Run(args);

        if (Environment.GetEnvironmentVariable("QDMAP_CHILD") == "1")
        {
            // started by the map to build its data: it reads these pipes as UTF-8
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        }
        else Win.AttachParentConsole();

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: <Paks folder> <command> [args]   (no arguments: start the map)");
            return 2;
        }
        try { return Command(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e);
            return 1;
        }
    }

    private static int Command(string[] args)
    {
        var paksDir = args[0];
        var cmd = args[1];
        var rest = args.Skip(2).ToArray();

        if (cmd == "export") Progress.Report(0, "Opening the game's files");
        var version = new VersionContainer(EGame.GAME_UE4_27);
        Provider = new CachingProvider(paksDir, version);
        Provider.Initialize();
        Provider.Mount();
        try { Provider.LoadLocalization(ELanguage.English); } catch (Exception e) { Console.Error.WriteLine("localization: " + e.Message); }
        Console.Error.WriteLine($"mounted: {Provider.Files.Count} files");

        switch (cmd)
        {
            case "dump": return Inspect.Dump(rest);
            // script <packagePath> <out.json> [nameFilter] : like dump, but with the blueprint bytecode of every function
            case "script": Provider.ReadScriptData = true; return Inspect.Dump(rest);
            // scriptdir <contentFolder> <outDir> [nameFilter] : script dump of every package under a folder, one JSON each
            case "scriptdir": Provider.ReadScriptData = true; return Inspect.DumpDir(rest);
            case "classes": return Inspect.Classes(rest);
            case "export": return WorldExporter.Run(rest);
            // stats <out folder> : only the gear stats page's data (stats-src.json), which export writes as well
            case "stats": return Stats.Run(rest);
            case "probe": return Probe.Run(rest);
            case "dbg": return Dbg.Run(rest);
            case "whoref": return Refs.Run(rest);
            case "graph": return Refs.Graph(rest);
            case "grep": return Refs.Grep(rest);
            case "names": return Refs.Names(rest);
            case "grepall": return Refs.GrepAll(rest);
            case "rawgrep": return Refs.RawGrep(rest);
            default:
                Console.Error.WriteLine("unknown command " + cmd);
                return 2;
        }
    }
}

public static class Inspect
{
    // dump <packagePath> <out.json> [nameFilter]
    public static int Dump(string[] a)
    {
        var pkg = Program.Provider.LoadPackage(a[0]);
        IEnumerable<UObject> exports = pkg.GetExports();
        if (a.Length > 2) exports = exports.Where(e => e.Name.Contains(a[2], StringComparison.OrdinalIgnoreCase) || e.ExportType.Contains(a[2], StringComparison.OrdinalIgnoreCase));
        var json = JsonConvert.SerializeObject(exports.ToArray(), Formatting.Indented);
        File.WriteAllText(a[1], json);
        Console.Error.WriteLine($"wrote {a[1]} ({json.Length / 1024} KB)");
        return 0;
    }

    // scriptdir <contentFolder> <outDir> [nameFilter] : skips textures, meshes and materials (anything without properties or bytecode worth reading)
    public static int DumpDir(string[] a)
    {
        var prefix = a[0].Replace('\\', '/').TrimEnd('/') + "/";
        Directory.CreateDirectory(a[1]);
        var skip = new HashSet<string> { "Texture2D", "StaticMesh", "SkeletalMesh", "Material", "MaterialInstanceConstant", "SoundWave", "AnimSequence", "Skeleton", "PhysicsAsset", "NiagaraSystem", "ParticleSystem", "SoundCue", "AnimMontage" };
        var n = 0;
        foreach (var file in Program.Provider.Files.Values.ToArray())
        {
            var path = file.Path;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (a.Length > 2 && !path.Contains(a[2], StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var exports = Program.Provider.LoadPackage(file).GetExports().ToArray();
                if (exports.Length == 0 || exports.All(e => skip.Contains(e.ExportType))) continue;
                var name = path[prefix.Length..^7].Replace('/', '~');
                File.WriteAllText(Path.Combine(a[1], name + ".json"), JsonConvert.SerializeObject(exports, Formatting.Indented));
                n++;
            }
            catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); }
        }
        Console.Error.WriteLine($"wrote {n} packages to {a[1]}");
        return 0;
    }

    // classes <packagePath> : histogram of export classes
    public static int Classes(string[] a)
    {
        foreach (var path in a)
        {
            var pkg = Program.Provider.LoadPackage(path);
            var counts = new Dictionary<string, int>();
            for (var i = 0; i < pkg.ExportMapLength; i++)
            {
                var ro = pkg.ResolvePackageIndex(new CUE4Parse.UE4.Objects.UObject.FPackageIndex(pkg, i + 1));
                var cls = ro?.Class?.Name.Text ?? "?";
                counts[cls] = counts.GetValueOrDefault(cls) + 1;
            }
            Console.WriteLine($"### {path} exports={pkg.ExportMapLength}");
            foreach (var kv in counts.OrderByDescending(k => k.Value))
                Console.WriteLine($"{kv.Value,7}  {kv.Key}");
        }
        return 0;
    }
}

public static class Probe
{
    // probe <levelId|packagePath> <classChainName> [max] : print merged (instance + archetype) properties of matching actors
    public static int Run(string[] a)
    {
        var skip = new HashSet<string> { "UberGraphFrame", "BlueprintCreatedComponents", "DefaultSceneRoot", "RootComponent", "SaveHelper" };
        var max = a.Length > 2 ? int.Parse(a[2]) : 2;
        IEnumerable<UObject> objs;
        if (a[0].EndsWith(".uasset"))
        {
            objs = Program.Provider.LoadPackage(a[0]).GetExports().Where(e => a[1] == "*" || e.ExportType.Contains(a[1]) || e.Name.Contains(a[1]));
        }
        else
        {
            var lr = WorldExporter.DiscoverLevels().First(l => l.Id.Contains(a[0], StringComparison.OrdinalIgnoreCase));
            var ctx = WorldExporter.LoadLevel(lr);
            objs = ctx.Actors.Where(x => P.ClassChain(x).Contains(a[1]));
        }
        var n = 0;
        foreach (var o in objs)
        {
            if (n++ >= max) break;
            Console.WriteLine($"## {o.ExportType} {o.Name}   chain: {string.Join(" < ", P.ClassChain(o))}");
            var seen = new HashSet<string>();
            var depth = 0;
            for (var t = o; t != null && depth < 16; t = P.Tmpl(t), depth++)
            {
                foreach (var p in t.Properties)
                {
                    var name = p.Name.Text;
                    if (skip.Contains(name) || !seen.Add(name)) continue;
                    if (p.Tag?.GenericValue is CUE4Parse.UE4.Objects.UObject.FPackageIndex pi && P.Obj(pi) is { } po && ReferenceEquals(P.Outer(po), t)) continue;
                    string json;
                    try { json = JsonConvert.SerializeObject(p.Tag, Formatting.None); }
                    catch (Exception e) { json = "<" + e.Message + ">"; }
                    if (json.Length > 700) json = json[..700] + "...";
                    Console.WriteLine($"   [{depth}] {name} = {json}");
                }
            }
        }
        return 0;
    }
}

public static class Dbg
{
    public static int Run(string[] a)
    {
        var lr = WorldExporter.DiscoverLevels().First(l => l.Id.Contains(a[0], StringComparison.OrdinalIgnoreCase));
        var ctx = WorldExporter.LoadLevel(lr);
        var set = new HashSet<UObject>(ctx.Actors, ReferenceEqualityComparer.Instance);
        foreach (var actor in ctx.Actors.Where(x => P.ClassChain(x).Contains(a[1])).Take(2))
        {
            Console.WriteLine($"## {actor.ExportType} {actor.Name}");
            foreach (var p in actor.Properties)
            {
                var gv = p.Tag?.GenericValue;
                Console.WriteLine($"   {p.Name.Text}: tag={p.Tag?.GetType().Name} gv={gv?.GetType().Name}");
                if (gv is CUE4Parse.UE4.Assets.Objects.UScriptArray arr)
                    foreach (var el in arr.Properties)
                    {
                        var ev = el?.GenericValue;
                        var obj = ev is CUE4Parse.UE4.Objects.UObject.FPackageIndex pi ? P.Obj(pi) : null;
                        Console.WriteLine($"        el tag={el?.GetType().Name} gv={ev?.GetType().Name} obj={obj?.Name} inSet={(obj != null && set.Contains(obj))}");
                    }
            }
            var ok = P.TryGet<CUE4Parse.UE4.Objects.UObject.FPackageIndex[]>(actor, a.Length > 2 ? a[2] : "ActorsToActivateOnOpen", out var acts);
            Console.WriteLine($"   TryGet => {ok} len={acts?.Length}");
        }
        return 0;
    }
}
