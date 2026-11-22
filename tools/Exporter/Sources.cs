using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;

namespace QDExport;

/// <summary>
/// Works out where items come from when they are not simply lying in a level: Witch's Grimoire inscriptions, boss rewards
/// (bosses spawn a "DummyNPC" whose RewardList hands out pickups), NPC give-actions, weapon evolutions, quest rewards and
/// so on. Uses the game's package reference graph plus the class defaults of the blueprints involved.
/// </summary>
public sealed partial class MarkerCollector
{
    // itemIdx -> [text, markerId or null, kind]
    private readonly Dictionary<int, List<object[]>> _sources = new();
    private Dictionary<string, HashSet<string>> _fwd;
    private Dictionary<string, List<string>> _back;
    private Dictionary<string, GameFile> _files;
    private readonly Dictionary<string, UObject> _cdo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UObject> _asset = new(StringComparer.OrdinalIgnoreCase);

    private void Src(int idx, string text, string markerId = null, string kind = null)
    {
        if (!_sources.TryGetValue(idx, out var l)) _sources[idx] = l = new List<object[]>();
        if (l.Count < 8 && !l.Any(x => (string) x[0] == text)) l.Add(new object[] { text, markerId, kind });
    }

    private void BuildGraph()
    {
        _fwd = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        _back = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _files = new Dictionary<string, GameFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Program.Provider.Files.Values.ToArray())
        {
            var path = file.Path;
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            var self = Refs.GamePath(path);
            _files[self] = file;
            try
            {
                if (Program.Provider.LoadPackage(file) is not Package pkg) continue;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var nm in pkg.NameMap)
                {
                    var s = nm.Name;
                    if (s == null || !s.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) continue;
                    var dot = s.IndexOf('.');
                    if (dot > 0) s = s[..dot];
                    if (!s.Equals(self, StringComparison.OrdinalIgnoreCase)) set.Add(s);
                }
                _fwd[self] = set;
                foreach (var s in set)
                {
                    if (!_back.TryGetValue(s, out var l)) _back[s] = l = new List<string>();
                    l.Add(self);
                }
            }
            catch { /* unreadable: skip */ }
        }
        Console.Error.WriteLine($"reference graph: {_fwd.Count} packages");
        Progress.Report(WorldExporter.PItems.From + 0.15, "Finding out where every item comes from");
    }

    private IEnumerable<string> Fwd(string pkg) => pkg != null && _fwd.TryGetValue(pkg, out var s) ? s : Enumerable.Empty<string>();
    private IEnumerable<string> Back(string pkg) => pkg != null && _back.TryGetValue(pkg, out var l) ? l : Enumerable.Empty<string>();
    private static string Leaf(string pkg) => pkg[(pkg.LastIndexOf('/') + 1)..];
    private static string Folder(string pkg) => pkg[..pkg.LastIndexOf('/')];
    private static string PkgOf(UObject o) => o == null ? null : Refs.GamePath(o.GetPathName());
    private static string PkgOf(ResolvedObject o) => o == null ? null : Refs.GamePath(o.GetPathName());

    /// <summary>Class default object of the blueprint class in package <paramref name="pkg"/> (null if it is not a blueprint).</summary>
    private UObject Cdo(string pkg)
    {
        if (pkg == null) return null;
        if (_cdo.TryGetValue(pkg, out var c)) return c;
        UObject res = null;
        try
        {
            if (_files.TryGetValue(pkg, out var f) && Program.Provider.LoadPackage(f) is { } p)
            {
                var want = Leaf(pkg) + "_C";
                for (var i = 0; i < p.ExportMapLength; i++)
                {
                    var ro = p.ResolvePackageIndex(new FPackageIndex(p, i + 1));
                    if (ro?.Name.Text != want) continue;
                    if (p.ExportsLazy[i].Value is UClass cls) res = P.Obj(cls.ClassDefaultObject);
                    break;
                }
            }
        }
        catch { /* ignored */ }
        return _cdo[pkg] = res;
    }

    /// <summary>The main asset of a package (the export named like the package).</summary>
    private UObject Asset(string pkg)
    {
        if (pkg == null) return null;
        if (_asset.TryGetValue(pkg, out var a)) return a;
        UObject res = null;
        try
        {
            if (_files.TryGetValue(pkg, out var f) && Program.Provider.LoadPackage(f) is { } p)
            {
                var want = Leaf(pkg);
                for (var i = 0; i < p.ExportMapLength; i++)
                {
                    var ro = p.ResolvePackageIndex(new FPackageIndex(p, i + 1));
                    if (ro?.Name.Text != want) continue;
                    res = p.ExportsLazy[i].Value;
                    break;
                }
            }
        }
        catch { /* ignored */ }
        return _asset[pkg] = res;
    }

    private string[] Chain(string pkg) => Cdo(pkg) is { } c ? P.ClassChain(c) : Array.Empty<string>();
    private bool IsPickupClass(string pkg) => Chain(pkg).Contains("ItemPickup_Base_C");
    private bool IsNpcClass(string pkg) => Chain(pkg).Contains("NPC_Base_C");
    private List<int> PickupItems(string pkg) => Cdo(pkg) is { } c ? ItemMap(c, "ItemsToGrant").Select(x => x[0]).ToList() : new List<int>();

    private static bool IsGhost(Marker m) => m.Cls.Contains("Ghost", StringComparison.OrdinalIgnoreCase) || m.Cls.Contains("Spirit", StringComparison.OrdinalIgnoreCase);

    private string EnemyMarker(int enemyIdx) =>
        Markers.Where(m => m.Enemy == enemyIdx).OrderBy(m => m.Type == "boss" ? 0 : 1).FirstOrDefault()?.Id;

    private string ItemMarker(int itemIdx) =>
        Markers.FirstOrDefault(m => m.Type == "item" && m.Items != null && m.Items.Any(x => x[0] == itemIdx))?.Id;

    private static readonly HashSet<string> FlagNoise = new(StringComparer.OrdinalIgnoreCase)
        { "first", "time", "speak", "state", "done", "quest", "gave", "give", "defeated", "acknowledged", "the", "has", "got", "complete", "completed" };

    private static List<string> Tokens(string s)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(s.Replace('-', '_'), @"_|(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])");
        return parts.Where(p => p.Length >= 3 && !FlagNoise.Contains(p)).ToList();
    }

    public void TraceSources(HashSet<int> placed)
    {
        BuildGraph();
        var itemByPkg = new Dictionary<string, ItemDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in Items) itemByPkg.TryAdd(Refs.GamePath(i.Path), i);
        bool Open(ItemDef i) => !placed.Contains(i.Idx) && !_sources.ContainsKey(i.Idx);
        var npcMarkers = Markers.Where(m => m.Type == "npc").Select(m => (m, cls: PkgOf(m.Actor?.Class))).Where(x => x.cls != null).ToList();

        // 1. Witch's Grimoire: each inscription (stored only as a hash) grants items
        var grimoireItem = itemByPkg.GetValueOrDefault("/Game/Items/Consumables/Grimoire/Grimoire_Item");
        var grimoireMarker = grimoireItem != null ? ItemMarker(grimoireItem.Idx) : null;
        var grimoireCount = 0;
        if (Cdo("/Game/Items/Consumables/Grimoire/Grimoire_Logic") is { } logic && P.FindTag(logic, "Passwords")?.Tag?.GenericValue is UScriptMap pw)
        {
            foreach (var (_, val) in pw.Properties)
            {
                if (AsStruct(val?.GenericValue) is not { } st) continue;
                grimoireCount++;
                foreach (var p in st.Properties.Where(p => p.Name.Text.StartsWith("ItemToGrant")))
                    if (p.Tag?.GenericValue is UScriptArray arr)
                        foreach (var el in arr.Properties)
                            if (ItemFromValue(el?.GenericValue) is { } d) Src(d.Idx, "Witch's Grimoire: write the right inscription", grimoireMarker, "grimoire");
            }
        }

        // 2. weapon evolutions: <weapon>_Item -> <weapon>_WeaponStats.Evolution
        foreach (var b in Items.ToArray())
        {
            if (b.Name.StartsWith("Null")) continue;
            foreach (var s in Fwd(Refs.GamePath(b.Path)).Where(s => Leaf(s).Contains("Stats", StringComparison.OrdinalIgnoreCase)).ToArray())
                if (Asset(s) is { } stats && P.TryGet<FPackageIndex>(stats, "Evolution", out var evo) && ItemFromValue(evo) is { } e && e != b)
                    Src(e.Idx, $"Evolution of the {b.Name}", ItemMarker(b.Idx), "evolve");
        }

        // 3. every enemy definition in the game (bosses spawn a DummyNPC that hands out rewards)
        var enemiesBefore = Enemies.Count;
        var enemyData = new List<(EnemyDef def, string data, string bp)>();
        foreach (var pkg in _files.Keys.Where(k => k.StartsWith("/Game/Enemies/", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(pkg, "Demo|Test|Bestiary", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            if (Asset(pkg) is not { } data || data.ExportType != "EnemyData_C") continue;
            var def = Enemy(data);
            var bp = P.TryGet<FPackageIndex>(data, "EnemyBP", out var bpi) ? PkgOf(P.Obj(bpi)) : null;
            enemyData.Add((def, pkg, bp));
        }
        foreach (var (def, _, bp) in enemyData)
        {
            var refs = Fwd(bp).Where(r => r.StartsWith("/Game/Enemies/", StringComparison.OrdinalIgnoreCase) || r.StartsWith("/Game/Items/", StringComparison.OrdinalIgnoreCase)).ToList();
            var dummies = refs.Where(IsNpcClass).ToList();
            dummies = dummies.Where(d => !dummies.Any(o => o != d && Chain(o).Contains(Leaf(d) + "_C"))).ToList(); // keep the most derived
            var got = new List<int>();
            foreach (var d in dummies) got.AddRange((NpcRewards(Cdo(d)) ?? new List<int[]>()).Select(x => x[0]));
            foreach (var r in refs.Where(IsPickupClass)) got.AddRange(PickupItems(r));
            foreach (var idx in got.Distinct()) Src(idx, $"Reward for defeating {def.Name}", EnemyMarker(def.Idx), "boss");
        }

        // 4a. NPC blueprints that reference a pickup class directly (e.g. Knight Budaen's KeyItemPickup)
        foreach (var (m, cls) in npcMarkers.OrderBy(x => IsGhost(x.m) ? 1 : 0))
            foreach (var r in Fwd(cls).Where(r => r.StartsWith("/Game/Items/", StringComparison.OrdinalIgnoreCase) && IsPickupClass(r)).ToArray())
                foreach (var idx in PickupItems(r))
                    if (!(_sources.GetValueOrDefault(idx)?.Any(s => (string) s[2] == "npc") ?? false) || !IsGhost(m))
                        Src(idx, $"Given by {m.Name}", m.Id, "npc");

        // 4b. dialogue "give" actions (…GiveKeyAction, …GiveKey) -> the NPC or enemy that owns the folder
        foreach (var a in _files.Keys.Where(k => (k.StartsWith("/Game/NPCs/", StringComparison.OrdinalIgnoreCase) || k.StartsWith("/Game/Enemies/", StringComparison.OrdinalIgnoreCase))
                                                 && Leaf(k).Contains("Give", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var pickups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in Fwd(a))
            {
                if (IsPickupClass(x)) { pickups.Add(x); continue; }
                if (!x.StartsWith("/Game/NPCs/", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("/Game/Enemies/", StringComparison.OrdinalIgnoreCase)) continue;
                var second = Fwd(x).Where(y => y.StartsWith("/Game/Items/", StringComparison.OrdinalIgnoreCase) && IsPickupClass(y)).ToList();
                if (second.Count <= 4) foreach (var y in second) pickups.Add(y);
            }
            if (pickups.Count == 0) continue;
            var folder = Folder(a);
            var owners = new List<(string name, string marker, string kind)>();
            foreach (var (m, cls) in npcMarkers.Where(x => Folder(x.cls).Equals(folder, StringComparison.OrdinalIgnoreCase)).OrderBy(x => IsGhost(x.m) ? 1 : 0))
                if (!owners.Any(o => o.name == m.Name) && !IsGhost(m)) owners.Add((m.Name, m.Id, "npc"));
            if (owners.Count == 0)
                foreach (var (def, data, _) in enemyData.Where(e => Folder(e.data).Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    if (!owners.Any(o => o.name == def.Name)) owners.Add((def.Name, EnemyMarker(def.Idx), "npc"));
            foreach (var p in pickups)
                foreach (var idx in PickupItems(p))
                    foreach (var o in owners.Take(3))
                        Src(idx, $"Given by {o.name}", o.marker, o.kind);
        }

        // 5. quest rewards the game re-grants from save flags (PlayerBalance.QuestStateFixups)
        if (Asset("/Game/Player/PlayerBalance") is { } bal && P.FindTag(bal, "QuestStateFixups")?.Tag?.GenericValue is UScriptArray fixes)
        {
            foreach (var el in fixes.Properties)
            {
                if (AsStruct(el?.GenericValue) is not { } st) continue;
                var flags = new List<string>();
                var rewards = new List<ItemDef>();
                foreach (var p in st.Properties)
                {
                    if (p.Name.Text.StartsWith("Conditions") && p.Tag?.GenericValue is UScriptArray conds)
                        foreach (var c in conds.Properties)
                            if (AsStruct(c?.GenericValue) is { } cs && cs.TryGetValue(out FName flag, "DataToCheck") && !flag.IsNone) flags.Add(flag.Text);
                    if (p.Name.Text.StartsWith("Rewards") && p.Tag?.GenericValue is UScriptMap rm)
                        foreach (var (k, _) in rm.Properties)
                            if (ItemFromValue(k?.GenericValue) is { } d) rewards.Add(d);
                }
                if (rewards.Count == 0 || flags.Count == 0) continue;
                var toks = Tokens(flags[0]);
                int Score(string s) => toks.Count(t => s.Contains(t, StringComparison.OrdinalIgnoreCase));
                var npc = npcMarkers.Where(x => !IsGhost(x.m)).Select(x => (x.m, s: Score(x.m.Cls) + Score(x.m.Name))).Where(x => x.s > 0).OrderByDescending(x => x.s).FirstOrDefault();
                var foe = enemyData.Select(e => (e.def, s: Score(e.data))).Where(x => x.s > 0).OrderByDescending(x => x.s).FirstOrDefault();
                foreach (var d in rewards.Where(Open))
                {
                    if (npc.m != null) Src(d.Idx, $"Given by {npc.m.Name}", npc.m.Id, "npc");
                    else if (foe.def != null) Src(d.Idx, $"Reward for defeating {foe.def.Name}", EnemyMarker(foe.def.Idx), "boss");
                    else Src(d.Idx, $"Quest reward ({Pretty(flags[0])})", null, "quest");
                }
            }
        }

        // 6. drop tables of enemies that no level spawner references (mimics live inside chests, …)
        foreach (var e in Enemies.Skip(enemiesBefore))
            foreach (var d in e.Drops)
                if (Items[(int) d[0]] is { } it && Open(it))
                    Src(it.Idx, $"Dropped by {e.Name} ({(float) d[1] * 100:0.#}%)", EnemyMarker(e.Idx), "drop");

        // 7. placed points of interest whose blueprint produces the item (e.g. the Super Cube Altar)
        foreach (var m in Markers.Where(m => m.Type is "poi" or "npc"))
            foreach (var r in Fwd(PkgOf(m.Actor?.Class)))
                if (itemByPkg.TryGetValue(r, out var it) && Open(it))
                    Src(it.Idx, $"Obtained at the {m.Name}", m.Id, "place");

        foreach (var it in Items.Where(Open).ToArray())
        {
            var pkg = Refs.GamePath(it.Path);
            // 8. the Challenges menu hands out a reward of its own
            if (Back(pkg).Any(r => r.StartsWith("/Game/UI/Menus/Challenges/", StringComparison.OrdinalIgnoreCase)))
                Src(it.Idx, "Reward from the Challenges menu", null, "challenge");
            // 9. a pickup class for the item that is spawned by a placed pickup type (food pedestals -> meal ticket)
            foreach (var pc in Back(pkg).Where(IsPickupClass).Where(pc => PickupItems(pc).Contains(it.Idx)))
                foreach (var spawner in Back(pc).Where(IsPickupClass))
                {
                    var cn = Leaf(spawner) + "_C";
                    var n = Markers.Count(m => m.Type == "item" && m.Actor != null && P.ClassChain(m.Actor).Contains(cn));
                    if (n > 0) Src(it.Idx, $"Can come from a {Pretty(cn).Replace(" Base", "")} ({n} on the map)", null, "place");
                }
        }

        // 10. drop-table entries listed with a 0% base chance. For mimics that is on purpose: Mimic_BP never rolls
        //     the first slot, it hands that item over when the defeated mimic is opened like a chest. (A mimic's treasure
        //     already has its own marker by now and is skipped here; this covers what else mimics hold.)
        foreach (var (data, def) in _enemyByObj.ToArray())
        {
            if (!P.TryGet<FStructFallback>(data, "Drop", out var drop)) continue;
            var mimic = def.Name.Contains("Mimic", StringComparison.OrdinalIgnoreCase);
            foreach (var p in drop.Properties.Where(p => p.Name.Text.StartsWith("Item")))
                if (ItemFromValue(p.Tag?.GenericValue) is { } it && Open(it))
                    Src(it.Idx, mimic ? $"Inside the {def.Name}: defeat it, then open the chest it leaves behind (guaranteed)"
                        : $"Listed in the {def.Name}'s drop table (0% base chance)", EnemyMarker(def.Idx), "drop");
        }

        // 11. hand-checked notes for sources that only exist in blueprint script or item text
        var notes = new Dictionary<string, (string text, string kind, string who)>(StringComparer.OrdinalIgnoreCase)
        {
            ["TridentKey_Consumed_Data"] = ("Comes out of the Stone Garden trident-key puzzle", "quest", null),
            ["BrassKey_Item"] = ("Item description: dropped by the Brass Drilling Unit on the Tower of Knowledge rooftop", "drop", "Brass Drilling Unit")
        };
        // told to us by a player; the game only does these in blueprint script
        var reported = new Dictionary<string, (string text, string kind, string who)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Umbada's Trading Outpost Key"] = ("Given by Trader Umbada once you have spent 1,000 coins in his shop", "npc", "Trader Umbada"),
            ["Vibrant Wind Gem"] = ("Granted automatically when you forge the final sword at the Secret Sword Altar", "quest", "Secret Sword Altar"),
            ["Holy Storm Gem"] = ("Granted automatically when you forge the final sword at the Secret Sword Altar", "quest", "Secret Sword Altar"),
            ["Mystical Nut of the White Tree"] = ("Dropped by the hidden White Tree Guardian", "boss", "White Tree Guardian")
        };
        foreach (var it in Items.Where(Open).ToArray())
            if (notes.TryGetValue(Leaf(Refs.GamePath(it.Path)), out var n) || reported.TryGetValue(it.Name ?? "", out n))
            {
                var who = n.who == null ? null : Enemies.FirstOrDefault(e => e.Name == n.who) is { } e ? EnemyMarker(e.Idx) : Markers.FirstOrDefault(m => m.Name == n.who)?.Id;
                Src(it.Idx, n.text, who, n.kind);
            }

        // 12. referenced by a level's own script (no actor in that level uses it as a key) -> scripted event there
        var levelIds = new HashSet<string>(Markers.Select(m => m.Level), StringComparer.OrdinalIgnoreCase);
        foreach (var it in Items.Where(Open).ToArray())
            foreach (var lv in Back(Refs.GamePath(it.Path)).Select(Leaf).Where(levelIds.Contains))
            {
                if (Markers.Any(m => m.Level == lv && m.Req != null && m.Req.Contains(it.Idx))) continue;
                var region = LevelRegions.TryGetValue(lv, out var rc) && rc.Count > 0 ? rc.OrderByDescending(k => k.Value).First().Key : lv;
                var save = Markers.FirstOrDefault(m => m.Level == lv && m.Type is "save" or "teleport")?.Id;
                Src(it.Idx, $"Likely a scripted event in {region}", save, "quest");
            }

        // 13. currency
        foreach (var it in Items.Where(i => i.Cat == "Coins" && Open(i)).ToArray())
            Src(it.Idx, "Currency: dropped by enemies and found in pots", null, "drop");

        var left = Items.Where(Open).Where(i => !System.Text.RegularExpressions.Regex.IsMatch(i.Name, @"^[{]|^Null |^No Items$|Added Desc$")).Select(i => i.Name).ToList();
        Console.Error.WriteLine($"sources: grimoire inscriptions={grimoireCount}, enemies scanned={enemyData.Count}, still unknown={left.Count}: {string.Join("; ", left)}");
    }
}
