using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QDExport;

/// <summary>
/// Writes stats-src.json for the gear stats page (stats.html): every item definition that is a weapon or an accessory,
/// the weapon and throwing-weapon stat assets as the game stores them, the weapon script classes with their parents
/// (to tell guns from melee weapons) and which items each Witch's Grimoire inscription gives. The page works the
/// numbers out from that.
/// </summary>
public static class Stats
{
    private const string Prefix = "QueensDomain/Content/Items/";
    private static readonly HashSet<string> StatTypes = new() { "FS_WeaponStats", "FS_DaggerStats" };
    private static readonly HashSet<string> ItemKeys = new()
    {
        "Item Name", "ImportedName", "Item Description", "StatData", "Category", "LogicComponent", "EquipSlot", "DataTags", "ItemTags",
    };

    // stats <out folder>
    public static int Run(string[] a)
    {
        if (a.Length < 1) { Console.Error.WriteLine("usage: <Paks folder> stats <out folder>"); return 2; }
        Directory.CreateDirectory(a[0]);
        Write(a[0]);
        return 0;
    }

    public static void Write(string outDir)
    {
        var packages = new JObject();
        var grimoire = new JArray();
        int items = 0, stats = 0, failed = 0;
        foreach (var file in Program.Provider.Files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var path = file.Path;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || !path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var pkg = Program.Provider.LoadPackage(file);
                var list = new JArray();
                for (var i = 0; i < pkg.ExportMapLength; i++)
                {
                    // look at the class first: most packages here are meshes and textures, which need not be read
                    var ro = pkg.ResolvePackageIndex(new FPackageIndex(pkg, i + 1));
                    var cls = ro?.Class?.Name.Text;
                    if (cls == null) continue;
                    var isItem = cls == "FS_ItemData_C";
                    var isStat = StatTypes.Contains(cls);
                    var isClass = cls == "BlueprintGeneratedClass";
                    var isGrimoire = ro.Name.Text == "Default__Grimoire_Logic_C";
                    if (!isItem && !isStat && !isClass && !isGrimoire) continue;
                    if (pkg.ExportsLazy[i].Value is not { } obj) continue;
                    var j = JObject.FromObject(obj);

                    if (isGrimoire)
                    {
                        // one list of item paths per inscription, in the game's order; the inscriptions themselves are not written
                        foreach (var entry in j["Properties"]?["Passwords"] as JArray ?? new JArray())
                        {
                            var grant = (entry["Value"] as JObject)?.Properties().FirstOrDefault(p => p.Name.StartsWith("ItemToGrant"))?.Value as JArray;
                            grimoire.Add(new JArray((grant ?? new JArray()).Select(v => v["ObjectPath"]).Where(v => v != null)));
                        }
                    }
                    else if (isClass) list.Add(new JObject { ["Type"] = "BlueprintGeneratedClass", ["Name"] = j["Name"], ["Super"] = j["Super"] });
                    else if (isStat) { list.Add(new JObject { ["Type"] = cls, ["Name"] = j["Name"], ["Properties"] = j["Properties"] ?? new JObject() }); stats++; }
                    else
                    {
                        var props = new JObject();
                        foreach (var p in (j["Properties"] as JObject ?? new JObject()).Properties())
                            if (ItemKeys.Contains(p.Name)) props[p.Name] = p.Value;
                        list.Add(new JObject { ["Type"] = cls, ["Name"] = j["Name"], ["Properties"] = props });
                        items++;
                    }
                }
                if (list.Count > 0) packages["/Game/Items/" + path[Prefix.Length..^7]] = list;
            }
            catch (Exception e)
            {
                failed++;
                Console.Error.WriteLine($"stats: {path}: {e.Message}");
            }
        }
        var root = new JObject { ["grimoire"] = grimoire, ["packages"] = packages };
        File.WriteAllText(Path.Combine(outDir, "stats-src.json"), root.ToString(Formatting.None));
        Console.Error.WriteLine($"gear stats: {items} item definitions, {stats} stat assets, {grimoire.Count} grimoire inscriptions{(failed > 0 ? $", {failed} packages failed" : "")}");
    }
}
