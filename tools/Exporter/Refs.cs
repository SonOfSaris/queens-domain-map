using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.UObject;

namespace QDExport;

/// <summary>
/// whoref [out.tsv] : for every item definition package, list the other packages whose name table mentions it
/// (covers hard imports and soft object paths). Output: itemPackage \t itemName \t referencingPackage
/// </summary>
public static class Refs
{
    public static string GamePath(string filePath)
    {
        var p = filePath.Replace('\\', '/');
        var i = p.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) p = "/Game/" + p[(i + 9)..];
        var dot = p.LastIndexOf('.');
        return dot > p.LastIndexOf('/') ? p[..dot] : p;
    }

    public static Dictionary<string, List<string>> Build(ICollection<string> targets)
    {
        var set = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        foreach (var file in Program.Provider.Files.Values.ToArray())
        {
            var path = file.Path;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            var self = GamePath(path);
            try
            {
                if (Program.Provider.LoadPackage(file) is not Package pkg) continue;
                scanned++;
                foreach (var n in pkg.NameMap)
                {
                    var s = n.Name;
                    if (s == null || !s.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) continue;
                    var dot = s.IndexOf('.');
                    if (dot > 0) s = s[..dot];
                    if (!set.Contains(s) || s.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!result.TryGetValue(s, out var l)) result[s] = l = new List<string>();
                    if (!l.Contains(self)) l.Add(self);
                }
            }
            catch { /* unreadable: skip */ }
        }
        Console.Error.WriteLine($"whoref: scanned {scanned} packages");
        return result;
    }

    /// <summary>grep &lt;levelId|*&gt; &lt;needle&gt;[,needle...] : actors in a level (* = every level) with a property whose name or serialized value mentions a needle</summary>
    public static int Grep(string[] a)
    {
        var all = WorldExporter.DiscoverLevels().ToList();
        var levels = a[0] == "*" ? all : all.Where(l => l.Id.Equals(a[0], StringComparison.OrdinalIgnoreCase)).ToList();
        var needles = a[1].Split(',');
        foreach (var lr in levels)
        {
            LevelCtx ctx;
            try { ctx = WorldExporter.LoadLevel(lr); } catch (Exception e) { Console.Error.WriteLine(lr.Id + ": " + e.Message); continue; }
            foreach (var actor in ctx.Actors)
            {
                foreach (var p in actor.Properties)
                {
                    string json;
                    try { json = Newtonsoft.Json.JsonConvert.SerializeObject(p.Tag); } catch { continue; }
                    foreach (var nd in needles)
                        if (json.Contains(nd, StringComparison.OrdinalIgnoreCase) || p.Name.Text.Contains(nd, StringComparison.OrdinalIgnoreCase))
                            Console.WriteLine(lr.Id + "	" + actor.ExportType + "	" + actor.Name + "	" + p.Name.Text + "	" + (json.Length > 300 ? json[..300] + "..." : json));
                }
            }
        }
        return 0;
    }

    /// <summary>
    /// How often each text occurs (as plain ASCII) in the unpacked bytes of every package file: name tables, string properties and
    /// script constants alike. Returns text -> (file -> count).
    /// </summary>
    public static Dictionary<string, Dictionary<string, int>> RawCount(ICollection<string> texts)
    {
        var pats = texts.Distinct().Select(t => (t, bytes: System.Text.Encoding.ASCII.GetBytes(t))).ToArray();
        var result = pats.ToDictionary(p => p.t, _ => new Dictionary<string, int>());
        foreach (var file in Program.Provider.Files.Values.ToArray())
        {
            var path = file.Path;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] data;
            try { data = file.Read(); } catch { continue; }
            foreach (var (t, bytes) in pats)
            {
                var n = 0;
                for (var span = data.AsSpan(); ;)
                {
                    var i = span.IndexOf(bytes);
                    if (i < 0) break;
                    n++;
                    span = span[(i + bytes.Length)..];
                }
                if (n > 0) result[t][path] = n;
            }
        }
        return result;
    }

    /// <summary>rawgrep &lt;text&gt;[,text...] : every package file whose unpacked bytes contain the text, with the number of occurrences</summary>
    public static int RawGrep(string[] a)
    {
        foreach (var (text, files) in RawCount(a[0].Split(',')))
        {
            Console.WriteLine(text + "	" + files.Values.Sum() + " in " + files.Count + " files");
            foreach (var (path, n) in files.OrderBy(f => f.Key)) Console.WriteLine("	" + n + "	" + path);
        }
        return 0;
    }

    /// <summary>names &lt;substr&gt;[,substr...] : packages whose name table contains a matching entry</summary>
    public static int Names(string[] a)
    {
        var needles = a[0].Split(',');
        foreach (var file in Program.Provider.Files.Values.ToArray())
        {
            var path = file.Path;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Program.Provider.LoadPackage(file) is not Package pkg) continue;
                var hits = pkg.NameMap.Select(n => n.Name).Where(n => n != null && needles.Any(nd => n.Contains(nd, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
                if (hits.Count > 0) Console.WriteLine($"{GamePath(path)}\t{string.Join(" | ", hits.Take(8))}");
            }
            catch { }
        }
        return 0;
    }

    /// <summary>grepall &lt;packagePath&gt; &lt;needle&gt; : every export of a package whose serialized form mentions needle</summary>
    public static int GrepAll(string[] a)
    {
        var pkg = Program.Provider.LoadPackage(a[0]);
        foreach (var e in pkg.GetExports())
        {
            string json;
            try { json = Newtonsoft.Json.JsonConvert.SerializeObject(e); } catch { continue; }
            var i = json.IndexOf(a[1], StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var outer = e.Outer?.Name ?? "";
            Console.WriteLine($"{e.ExportType}\t{e.Name}\touter={outer}\t...{json.Substring(Math.Max(0, i - 250), Math.Min(500, json.Length - Math.Max(0, i - 250)))}...");
        }
        return 0;
    }

    /// <summary>graph [out.tsv] : every package -> every /Game/ package its name table mentions</summary>
    public static int Graph(string[] a)
    {
        using var w = new StreamWriter(a.Length > 0 ? a[0] : "graph.tsv");
        var n = 0;
        foreach (var file in Program.Provider.Files.Values.ToArray())
        {
            var path = file.Path;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            var self = GamePath(path);
            try
            {
                if (Program.Provider.LoadPackage(file) is not Package pkg) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var nm in pkg.NameMap)
                {
                    var s = nm.Name;
                    if (s == null || !s.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) continue;
                    var dot = s.IndexOf('.');
                    if (dot > 0) s = s[..dot];
                    if (s.Equals(self, StringComparison.OrdinalIgnoreCase) || !seen.Add(s)) continue;
                    w.WriteLine($"{self}\t{s}");
                }
                n++;
            }
            catch { }
        }
        Console.Error.WriteLine($"graph: {n} packages");
        return 0;
    }

    public static int Run(string[] a)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Program.Provider.Files.Values.ToArray())
        {
            var path = file.Path;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.Contains("/Content/Items/", StringComparison.OrdinalIgnoreCase) && !path.Contains("/Content/Artifacts/", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var pkg = Program.Provider.LoadPackage(file);
                for (var i = 0; i < pkg.ExportMapLength; i++)
                {
                    var ro = pkg.ResolvePackageIndex(new FPackageIndex(pkg, i + 1));
                    if (ro?.Class?.Name.Text != "FS_ItemData_C" || ro.Name.Text.StartsWith("Default__")) continue;
                    var obj = pkg.ExportsLazy[i].Value;
                    string name = null;
                    try { name = P.Get<CUE4Parse.UE4.Objects.Core.i18N.FText>(obj, "Name")?.Text; } catch { }
                    items[GamePath(path)] = name ?? obj.Name;
                }
            }
            catch { }
        }
        var refs = Build(items.Keys);
        using var w = new StreamWriter(a.Length > 0 ? a[0] : "whoref.tsv");
        foreach (var (pkgPath, name) in items.OrderBy(k => k.Key))
        {
            if (!refs.TryGetValue(pkgPath, out var l)) { w.WriteLine($"{pkgPath}\t{name}\t"); continue; }
            foreach (var r in l) w.WriteLine($"{pkgPath}\t{name}\t{r}");
        }
        Console.Error.WriteLine($"whoref: {items.Count} items, {refs.Count} referenced");
        return 0;
    }
}
