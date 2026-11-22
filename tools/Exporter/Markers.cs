using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using SkiaSharp;

namespace QDExport;

public sealed class ItemDef
{
    public int Idx;
    public string Path, Name, Desc, Cat;
    public int Sort;
    public UTexture2D IconTex;
    public int Icon = -1;
}

public sealed class EnemyDef
{
    public int Idx;
    public string Path, Name, Desc;
    public float Hp;
    public int Xp;
    public List<object[]> Drops = new(); // [itemIdx, chance, count]
    public int Held = -1;                // mimics: the item handed over when the defeated mimic is opened (first drop slot, never rolled)
}

public sealed class Marker
{
    public string Id, Type, Level, Cls, Name, Desc, Container, Diff, Note;
    public float[] Pos;
    public List<int[]> Items;   // [itemIdx, count]
    public List<int> Req;       // required item indices
    public int Enemy = -1;
    public bool Boss, Hidden;
    public List<object[]> Shop; // [itemIdx, price, stock (-1 = unlimited), requiredProgression]
    public int By = -1;          // item markers for something a one-time enemy carries: that enemy
    public string How;           // ...and how it changes hands: "drop" (on defeat) or "open" (mimics)
    public object[] Spawn;       // enemy spawners with a condition: [save flag, min, max, note when the flag can never be set]
    public List<string> Guids;   // save-game ids of the actor (keys of the save file's SavableActorStates)
    public string Asset;         // data asset or note key the save file refers to (save point, teleporter, NPC, note)
    public int MaxKills;         // enemy spawners that stay dead after this many kills
    public UObject Actor;
}

public sealed partial class MarkerCollector
{
    public readonly List<ItemDef> Items = new();
    public readonly List<EnemyDef> Enemies = new();
    public readonly List<Marker> Markers = new();
    public readonly Dictionary<string, Dictionary<string, int>> LevelRegions = new();
    private readonly Dictionary<string, ItemDef> _itemByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<UObject, EnemyDef> _enemyByObj = new(ReferenceEqualityComparer.Instance);

    private static readonly string[] PoiClasses =
    {
        "WeaponUpgradeMachine_C", "SuperCubeAltar_C", "SpectralSwordAltar_C", "SecretSword_Altar_BP_C", "SecretSkullRitual_BP_C",
        "Shopkeeper_C", "EggDoor_BP_C", "BigNut_Interact_C", "ElecPuzzleTerminal_C", "Lib_Bell_00_BP_C", "UpgradeMachine_C"
    };

    // Save flags that spawners wait for but nothing in the game ever sets, so the enemy never appears. Hand-checked for the
    // 14 Nov 2026 build with `grep * <flag>`, `names <flag>` and `rawgrep <flag>`: the spawn condition is the only mention.
    // Keyed by the flag, so an entry stops applying by itself once the developers correct the spawner.
    private static readonly Dictionary<string, string> NeverSetFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NecroWall2Destroyed"] = "Nothing in the game sets this flag: the flesh wall beside it sets NecroWall02Destroyed, as the other enemies " +
                                  "behind that wall expect. It looks like a typo in the game, and this enemy never appears (14 Nov 2026 build)."
    };

    // ------------------------------------------------------------------ helpers

    private static string Text(UObject o, string prop)
    {
        try { return P.Get<FText>(o, prop)?.Text?.Trim(); }
        catch { return null; }
    }

    public static string Pretty(string cls)
    {
        var s = cls;
        if (s.EndsWith("_C")) s = s[..^2];
        foreach (var junk in new[] { "_Spawner", "_NPC_BP", "_NPC", "_BP", "_Bp", "_Pickup", "_Item", "Spawner" })
            s = s.Replace(junk, "");
        s = s.Replace('_', ' ');
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        var res = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return res.Replace("Recepticle", "Receptacle").Replace("AQR ", "").Replace("AQ ", "").Replace("LIB ", "").Replace("SG ", "");
    }

    private static string CategoryOf(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Contains("/Treasure/Coins/", StringComparison.OrdinalIgnoreCase)) return "Coins";
        var i = p.IndexOf("/Items/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var rest = p[(i + 7)..];
            var j = rest.IndexOf('/');
            if (j > 0) return rest[..j];
        }
        if (p.Contains("/Artifacts/", StringComparison.OrdinalIgnoreCase)) return "Artifacts";
        return "Other";
    }

    private ItemDef Item(UObject data)
    {
        if (data == null) return null;
        var path = data.GetPathName();
        if (_itemByPath.TryGetValue(path, out var def)) return def;
        def = new ItemDef
        {
            Idx = Items.Count,
            Path = path,
            Name = Text(data, "Name"),
            Desc = Text(data, "Item Description"),
            Cat = CategoryOf(path),
            Sort = P.Get(data, "SortOrder", 0),
            IconTex = P.Obj(P.Get<FPackageIndex>(data, "ItemIcon")) as UTexture2D
        };
        if (string.IsNullOrWhiteSpace(def.Name)) def.Name = Pretty(data.Name);
        Items.Add(def);
        _itemByPath[path] = def;
        return def;
    }

    private ItemDef ItemFromValue(object v)
    {
        try
        {
            switch (v)
            {
                case FPackageIndex pi:
                    return Item(P.Obj(pi));
                case FSoftObjectPath sp:
                    if (sp.AssetPathName.IsNone || sp.AssetPathName.Text == "None") return null;
                    return sp.TryLoad(Program.Provider, out var exp) ? Item(exp as UObject) : null;
            }
        }
        catch { /* ignored */ }
        return null;
    }

    private List<int[]> ItemMap(UObject actor, string prop)
    {
        var list = new List<int[]>();
        if (!P.TryGet<UScriptMap>(actor, prop, out var map) || map?.Properties == null) return list;
        foreach (var (k, val) in map.Properties)
        {
            var def = ItemFromValue(k?.GenericValue);
            if (def == null) continue;
            var count = val?.GenericValue is int c ? c : 1;
            list.Add(new[] { def.Idx, count });
        }
        return list;
    }

    private List<int> ItemArray(UObject actor, string prop)
    {
        var list = new List<int>();
        foreach (var pi in P.Refs(actor, prop))
        {
            var def = ItemFromValue(pi);
            if (def != null && !list.Contains(def.Idx)) list.Add(def.Idx);
        }
        return list;
    }

    private EnemyDef Enemy(UObject data)
    {
        if (data == null) return null;
        if (_enemyByObj.TryGetValue(data, out var def)) return def;
        def = new EnemyDef
        {
            Idx = Enemies.Count,
            Path = data.GetPathName(),
            Name = Text(data, "Name"),
            Desc = Text(data, "Description"),
            Hp = P.Get(data, "HP", 0f),
            Xp = P.Get(data, "XPReward", 0)
        };
        if (string.IsNullOrWhiteSpace(def.Name)) def.Name = Pretty(data.Name.Replace("_Data", ""));
        if (P.TryGet<FStructFallback>(data, "Drop", out var drop))
        {
            ParseDrop(drop, def.Drops);
            // Mimic_BP never rolls its first drop slot (listed at 0%): it hands that item over when the defeated mimic is opened
            if (def.Name.Contains("Mimic", StringComparison.OrdinalIgnoreCase) &&
                drop.Properties.FirstOrDefault(p => p.Name.Text.StartsWith("Item_")) is { } slot && ItemFromValue(slot.Tag?.GenericValue) is { } held)
                def.Held = held.Idx;
        }
        Enemies.Add(def);
        _enemyByObj[data] = def;
        return def;
    }

    private void ParseDrop(FStructFallback drop, List<object[]> into)
    {
        foreach (var suffix in new[] { "", "2" })
        {
            float chance = 0;
            var coins = -1;
            ItemDef item = null;
            foreach (var p in drop.Properties)
            {
                var n = p.Name.Text;
                if (n.StartsWith("Chance" + suffix + "_")) chance = p.Tag?.GenericValue is float f ? f : 0;
                else if (n.StartsWith("Item" + suffix + "_")) item = ItemFromValue(p.Tag?.GenericValue);
                else if (n.StartsWith("CoinCount" + suffix + "_")) coins = p.Tag?.GenericValue is int c ? c : -1;
            }
            if (item != null && chance > 0) into.Add(new object[] { item.Idx, MathF.Round(chance, 3), Math.Max(coins, 1) });
        }
    }

    private static FStructFallback AsStruct(object v) => (v as FScriptStruct)?.StructType as FStructFallback;

    /// <summary>Items an NPC hands out (RewardList entries point at pickup blueprints; read their default ItemsToGrant).</summary>
    private List<int[]> NpcRewards(UObject actor)
    {
        var list = new List<int[]>();
        void FromPickupClass(FPackageIndex cls)
        {
            if (P.Obj(cls) is not UClass c) return;
            var cdo = P.Obj(c.ClassDefaultObject);
            if (cdo == null) return;
            foreach (var it in ItemMap(cdo, "ItemsToGrant"))
                if (!list.Any(x => x[0] == it[0])) list.Add(it);
        }
        try
        {
            if (P.FindTag(actor, "RewardList")?.Tag?.GenericValue is UScriptArray arr)
                foreach (var el in arr.Properties)
                {
                    var st = AsStruct(el?.GenericValue);
                    if (st == null) continue;
                    foreach (var p in st.Properties)
                        if (p.Name.Text.StartsWith("ItemPickup_") && p.Tag?.GenericValue is FPackageIndex pi) FromPickupClass(pi);
                }
            if (P.TryGet<FPackageIndex>(actor, "MementoBP", out var memento)) FromPickupClass(memento);
        }
        catch { /* best effort */ }
        return list.Count > 0 ? list : null;
    }

    private List<object[]> ShopStock(UObject shop)
    {
        if (shop == null || !P.TryGet<UScriptMap>(shop, "Stock", out var stock) || stock?.Properties == null) return null;
        var list = new List<object[]>();
        foreach (var (_, val) in stock.Properties)
        {
            var st = AsStruct(val?.GenericValue);
            if (st == null) continue;
            var price = st.GetOrDefault("Price", 0);
            var infinite = st.GetOrDefault("bInfiniteStock", false);
            var baseStock = st.GetOrDefault("BaseStock", 1);
            var prog = st.GetOrDefault("RequiredProgression", 0);
            var granted = new List<ItemDef>();
            if (st.TryGetValue(out UScriptMap grant, "ItemsToGrant") && grant?.Properties != null)
                foreach (var (k, _) in grant.Properties)
                    if (ItemFromValue(k?.GenericValue) is { } d) granted.Add(d);
            if (granted.Count == 0 && st.TryGetValue(out FSoftObjectPath disp, "DisplayItem") && ItemFromValue(disp) is { } dd) granted.Add(dd);
            foreach (var d in granted) list.Add(new object[] { d.Idx, price, infinite ? -1 : baseStock, prog });
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>Register every item definition in the game, so items that are never placed in a level still show up in the guide.</summary>
    private void LoadAllItems()
    {
        var n = 0;
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
                    if (ro.Name.Text.EndsWith("_PickupConfig", StringComparison.OrdinalIgnoreCase)) continue; // display config, not an item
                    if (pkg.ExportsLazy[i].Value is { } obj && Item(obj) != null) n++;
                }
            }
            catch { /* unreadable asset: skip */ }
        }
        Console.Error.WriteLine($"item definitions scanned: {n}, total known: {Items.Count}");
    }

    // ------------------------------------------------------------------ collection

    public void Collect(LevelCtx ctx)
    {
        var actorSet = new HashSet<UObject>(ctx.Actors, ReferenceEqualityComparer.Instance);

        // reverse references: which actors point at which (chest -> contents, wall -> hint, ...)
        var refs = new Dictionary<UObject, List<(UObject src, string prop)>>(ReferenceEqualityComparer.Instance);
        void AddRef(UObject src, string prop, object v)
        {
            if (P.AsIndex(v) is not { IsNull: false } pi) return;
            var target = P.Obj(pi);
            if (target == null || ReferenceEquals(target, src) || !actorSet.Contains(target)) return;
            if (!refs.TryGetValue(target, out var l)) refs[target] = l = new List<(UObject, string)>();
            l.Add((src, prop));
        }
        foreach (var actor in ctx.Actors)
        {
            foreach (var p in actor.Properties)
            {
                var gv = p.Tag?.GenericValue;
                if (gv is FPackageIndex or FScriptInterface) AddRef(actor, p.Name.Text, gv);
                else if (gv is UScriptArray arr)
                    foreach (var el in arr.Properties) AddRef(actor, p.Name.Text, el?.GenericValue);
            }
        }

        foreach (var actor in ctx.Actors)
        {
            var chain = P.ClassChain(actor);
            if (chain.Length == 0) continue;
            Marker m = null;

            if (chain.Contains("ItemPickup_Base_C"))
            {
                var items = ItemMap(actor, "ItemsToGrant");
                if (items.Count == 0) continue;
                m = New(ctx, actor, "item");
                m.Items = items;
                m.Hidden = P.Get(actor, "RequireActivation", false);
                if (P.TryGet<FName>(actor, "PickupDifficulty", out var dn))
                {
                    var diff = dn.Text;
                    if (!string.IsNullOrEmpty(diff) && diff != "None") m.Diff = diff[(diff.LastIndexOf('_') + 1)..];
                }
                if (refs.TryGetValue(actor, out var srcs))
                {
                    foreach (var (src, prop) in srcs)
                    {
                        var sc = P.ClassChain(src);
                        string c = null;
                        if (sc.Contains("Chest_C"))
                        {
                            c = "Chest";
                            var key = ItemFromValue(P.Get<FPackageIndex>(src, "RequiredKey"));
                            if (key != null) (m.Req ??= new List<int>()).Add(key.Idx);
                        }
                        else if (sc.Contains("Pot_Base_C")) c = "Pot";
                        else if (sc.Contains("Permabreakable_base_C")) c = Pretty(src.ExportType);
                        else if (sc.Contains("EnemySpawner_C")) c = "Enemy: " + (Enemy(P.Obj(P.Get<FPackageIndex>(src, "EnemyData")))?.Name ?? Pretty(src.ExportType));
                        else if (sc.Contains("SecretWall_C")) c = "Secret wall";
                        else if (sc.Contains("NPC_Base_C")) c = "NPC: " + (Text(src, "name") ?? Pretty(src.ExportType));
                        else if (sc.Contains("ItemPickup_Base_C")) continue;
                        else if (prop.Contains("Activat")) c = Pretty(src.ExportType);
                        if (c == null) continue;
                        m.Container = m.Container == null ? c : m.Container.Contains(c) ? m.Container : m.Container + ", " + c;
                    }
                }
            }
            else if (chain.Contains("Chest_C"))
            {
                m = New(ctx, actor, "chest");
                m.Name = Pretty(actor.ExportType);
                var key = ItemFromValue(P.Get<FPackageIndex>(actor, "RequiredKey"));
                if (key != null) m.Req = new List<int> { key.Idx };
                var contents = new List<int[]>();
                var notes = new List<string>();
                {
                    foreach (var ai in P.Refs(actor, "ActorsToActivateOnOpen"))
                    {
                        var t = P.Obj(ai);
                        if (t == null) continue;
                        var tc = P.ClassChain(t);
                        if (tc.Contains("ItemPickup_Base_C")) contents.AddRange(ItemMap(t, "ItemsToGrant"));
                        else if (tc.Contains("EnemySpawner_C")) notes.Add("Ambush: " + (Enemy(P.Obj(P.Get<FPackageIndex>(t, "EnemyData")))?.Name ?? Pretty(t.ExportType)));
                    }
                }
                m.Items = contents;
                if (notes.Count > 0) m.Note = string.Join("; ", notes);
            }
            else if (chain.Contains("SecretWall_C"))
            {
                m = New(ctx, actor, "secret");
                m.Name = chain.Contains("ScienceSecretWall_C") ? "Secret wall (tech)" : actor.ExportType.Contains("BoringDoor") ? "Hidden door" : "Secret wall";
                if (P.Get(actor, "Don'tCountForAchievement", false)) m.Note = "Does not count for the secrets achievement";
            }
            else if (chain.Contains("EnemySpawner_C"))
            {
                var def = Enemy(P.Obj(P.Get<FPackageIndex>(actor, "EnemyData")));
                m = New(ctx, actor, "enemy");
                m.Enemy = def?.Idx ?? -1;
                m.Name = def?.Name ?? Pretty(actor.ExportType);
                // The spawner's ItemDropOverride is left out on purpose: EnemySpawner only copies it to its editor preview actor
                // (SetDropsOnPreviewActor), spawned enemies always roll their own data asset's drop table.
                if (P.Get(actor, "UseMaxKillCount", false)) m.MaxKills = Math.Max(1, P.Get(actor, "MaxKillCount", 1));
                if (P.Get(actor, "RequireNamedSaveDataToSpawn", false) && P.FindTag(actor, "NamedSaveDataToCheck")?.Tag?.GenericValue?.ToString() is { Length: > 0 } flag && flag != "None")
                {
                    m.Spawn = new object[] { flag, P.Get(actor, "NamedSaveDataMin", 0), P.Get(actor, "NamedSaveDataMax", 0) };
                    if (NeverSetFlags.TryGetValue(flag, out var why)) m.Spawn = m.Spawn.Append(why).ToArray();
                }
                var cls = actor.ExportType;
                m.Boss = P.Get(actor, "BossFightSpawner", false) || P.FindTag(actor, "Boss Group DA")?.Tag?.GenericValue is FPackageIndex { IsNull: false } ||
                         cls.Contains("Boss", StringComparison.OrdinalIgnoreCase);
                if (m.Boss) m.Type = "boss";
                if (cls.Contains("Miniboss", StringComparison.OrdinalIgnoreCase) && !m.Boss) { m.Type = "boss"; m.Note = "Miniboss"; }
            }
            else if (chain.Contains("NPC_Base_C"))
            {
                m = New(ctx, actor, "npc");
                m.Name = Text(actor, "name") ?? Pretty(actor.ExportType);
                m.Items = NpcRewards(actor);
                m.Asset = AssetRef(actor, "NPC Data");
                m.Shop = ShopStock(P.Obj(P.Get<FPackageIndex>(actor, "Shop Data")));
            }
            else if (chain.Contains("Checkpoint_C"))
            {
                m = New(ctx, actor, "save");
                var loc = P.Obj(P.Get<FPackageIndex>(actor, "LocationData"));
                m.Name = loc != null ? Text(loc, "AreaName") ?? Text(loc, "Name") : null;
                m.Name ??= "Save point";
                m.Asset = AssetRef(actor, "LocationData");
                var region = loc != null ? Text(loc, "Name") : null;
                if (region != null)
                {
                    m.Note = region;
                    if (!LevelRegions.TryGetValue(ctx.Ref.Id, out var rc)) LevelRegions[ctx.Ref.Id] = rc = new Dictionary<string, int>();
                    rc[region] = rc.GetValueOrDefault(region) + 1;
                }
            }
            else if (chain.Contains("Teleporter_C") || chain.Contains("MiniTeleporter_C"))
            {
                m = New(ctx, actor, "teleport");
                var loc = P.Obj(P.Get<FPackageIndex>(actor, "LocationData")) ?? P.Obj(P.Get<FPackageIndex>(actor, "LocationDA"));
                m.Name = (loc != null ? Text(loc, "Name") : null) ?? "Teleporter";
                m.Asset = AssetRef(actor, "LocationData") ?? AssetRef(actor, "LocationDA");
                m.Desc = loc != null ? Text(loc, "Description") : null;
                if (chain.Contains("MiniTeleporter_C")) m.Note = "Mini teleporter";
            }
            else if (chain.Contains("InspectText_C"))
            {
                m = New(ctx, actor, "lore");
                m.Name = Text(actor, "Title") ?? "Inscription";
                m.Desc = Text(actor, "Text");
                var noteKey = P.FindTag(actor, "ImportKey")?.Tag?.GenericValue?.ToString();
                if (!string.IsNullOrWhiteSpace(noteKey) && noteKey != "None") m.Asset = noteKey; // the save file records "ReadNote_<key>"
            }
            else if (chain.Contains("Door_Base_C"))
            {
                var keys = ItemMap(actor, "KeyItems");
                if (keys.Count == 0) continue;
                m = New(ctx, actor, "door");
                m.Name = "Locked door";
                m.Req = keys.Select(k => k[0]).ToList();
            }
            else if (chain.Contains("AQR_KeyRecepticle_BP_C"))
            {
                m = New(ctx, actor, "keyslot");
                m.Name = Pretty(actor.ExportType);
                var spec = P.Get(actor, "RequiresSpecifiedKey", false) ? ItemArray(actor, "SpecifiedKeys") : new List<int>();
                if (spec.Count == 0) spec = ItemArray(actor, "ValidKeys");
                if (spec.Count > 0) m.Req = spec;
            }
            else if (chain.Contains("Lever_C") || chain.Contains("ElevatorLever_C") || chain.Contains("Gargoyle_Switch_C"))
            {
                m = New(ctx, actor, "switch");
                m.Name = Pretty(actor.ExportType);
            }
            else if (chain.Contains("WallHint_Base_BP_C"))
            {
                m = New(ctx, actor, "hint");
                m.Name = "Wall hint";
            }
            else if (chain.Any(c => PoiClasses.Contains(c)))
            {
                m = New(ctx, actor, "poi");
                m.Name = Pretty(actor.ExportType);
            }

            if (m?.Pos != null) Markers.Add(m);
        }
    }

    private static Marker New(LevelCtx ctx, UObject actor, string type)
    {
        var m = new Marker { Id = ctx.Ref.Id + "/" + actor.Name, Type = type, Level = ctx.Ref.Id, Cls = actor.ExportType, Actor = actor, Guids = GuidsOf(actor) };
        var root = ctx.Root(actor);
        if (root == null) return m;
        var t = ctx.WorldXf(root).Translation;
        m.Pos = new[] { MathF.Round(t.X * 0.01f, 2), MathF.Round(t.Z * 0.01f, 2), MathF.Round(t.Y * 0.01f, 2) };
        return m;
    }

    /// <summary>Save-game ids of a placed actor ("GUIDs" on savable actors, "GUID" on NPCs and save points), as 32 hex digits.</summary>
    private static List<string> GuidsOf(UObject actor)
    {
        var list = new List<string>();
        void Add(object v)
        {
            if (v is FScriptStruct ss) v = ss.StructType;
            if (v is FGuid g && (g.A | g.B | g.C | g.D) != 0) list.Add($"{g.A:X8}{g.B:X8}{g.C:X8}{g.D:X8}");
        }
        try
        {
            if (P.FindTag(actor, "GUIDs")?.Tag?.GenericValue is UScriptArray arr)
                foreach (var el in arr.Properties) Add(el?.GenericValue);
            Add(P.FindTag(actor, "GUID")?.Tag?.GenericValue);
        }
        catch { /* no ids */ }
        return list.Count > 0 ? list : null;
    }

    /// <summary>/Game/... package path of the asset an object property points at.</summary>
    private static string AssetRef(UObject actor, string prop)
    {
        var path = P.PathOf(P.Get<FPackageIndex>(actor, prop));
        return string.IsNullOrEmpty(path) ? null : Refs.GamePath(path);
    }

    // ------------------------------------------------------------------ output

    public void Finish(string outDir, bool noTex)
    {
        LoadAllItems();
        // chest markers are only kept when the chest has nothing we already show as an item marker
        const int cell = 64, cols = 32;
        var withIcon = Items.Where(i => i.IconTex != null).ToList();
        if (!noTex && withIcon.Count > 0)
        {
            var rows = (withIcon.Count + cols - 1) / cols;
            using var atlas = new SKBitmap(new SKImageInfo(cols * cell, rows * cell, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            atlas.Erase(SKColors.Transparent);
            using var canvas = new SKCanvas(atlas);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            var options = new ExportOptions();
            var n = 0;
            foreach (var it in withIcon)
            {
                try
                {
                    var ct = it.IconTex.Decode(256, options);
                    if (ct == null) continue;
                    using var bmp = ct.ToSkBitmap();
                    var x = n % cols * cell;
                    var y = n / cols * cell;
                    canvas.DrawBitmap(bmp, new SKRect(x, y, x + cell, y + cell), paint);
                    it.Icon = n++;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"icon failed: {it.Name}: {e.Message}");
                }
            }
            canvas.Flush();
            using var data = atlas.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(Path.Combine(outDir, "icons.png"));
            data.SaveTo(fs);
            Console.Error.WriteLine($"icons: {n} in atlas");
            Progress.Report(WorldExporter.PItems.From + 0.03, "Finding out where every item comes from");
        }

        // ---- treasures carried by enemies that never come back: as good as a pickup at the enemy's spawn point ----
        // (the Star Lizards' loot sacks above all; also the guards' silver jewellery, the crowned slimes and the mimics)
        foreach (var m in Markers.Where(m => m.Type == "enemy" && m.Enemy >= 0 && m.MaxKills > 0).ToArray())
        {
            var def = Enemies[m.Enemy];
            var carried = def.Drops.Where(d => (float) d[1] >= 1f).Select(d => (item: (int) d[0], how: "drop")).ToList();
            if (def.Held >= 0) carried.Add((def.Held, "open"));
            var n = 0;
            foreach (var (item, how) in carried.Where(c => Items[c.item].Cat == "Treasure"))
                Markers.Add(new Marker
                {
                    Id = m.Id + "#loot" + (n++ > 0 ? n.ToString() : ""), Type = "item", Level = m.Level, Cls = m.Cls, Guids = m.Guids,
                    Pos = new[] { m.Pos[0], MathF.Round(m.Pos[1] + 1.2f, 2), m.Pos[2] }, // just above the enemy's own marker
                    Items = new List<int[]> { new[] { item, 1 } }, By = m.Enemy, How = how, Spawn = m.Spawn
                });
        }

        // ---- where to get items that are never placed as a pickup ----
        var placed = new HashSet<int>();
        foreach (var m in Markers.Where(m => m.Type is "item" or "chest"))
            foreach (var it in m.Items ?? new List<int[]>()) placed.Add(it[0]);
        foreach (var m in Markers.Where(m => m.Type == "npc"))
        {
            foreach (var sh in m.Shop ?? new List<object[]>()) Src((int) sh[0], $"Sold by {m.Name} for {sh[1]} coin{((int) sh[1] == 1 ? "" : "s")}", m.Id, "shop");
            foreach (var it in m.Items ?? new List<int[]>()) Src(it[0], $"Given by {m.Name}", m.Id, "npc");
        }
        foreach (var e in Enemies.ToArray())
            foreach (var d in e.Drops) Src((int) d[0], $"Dropped by {e.Name} ({(float) d[1] * 100:0.#}%)", null, "drop");
        TraceSources(placed);
        var sources = _sources;

        var json = new
        {
            iconCell = cell,
            iconCols = cols,
            items = Items.Select(i => new
            {
                n = i.Name, d = i.Desc, c = i.Cat, s = i.Sort, i = i.Icon, p = Refs.GamePath(i.Path),
                placed = placed.Contains(i.Idx) ? (bool?) true : null,
                src = sources.TryGetValue(i.Idx, out var sl) ? sl : null
            }),
            enemies = Enemies.Select(e => new { n = e.Name, d = e.Desc, hp = e.Hp, xp = e.Xp, drops = e.Drops, p = Refs.GamePath(e.Path) }),
            markers = Markers.Select(m => new
            {
                id = m.Id, t = m.Type, l = m.Level, p = m.Pos, n = m.Name, d = m.Desc, c = m.Container, df = m.Diff, note = m.Note,
                it = m.Items, rq = m.Req, shop = m.Shop, e = m.Enemy >= 0 ? (int?) m.Enemy : null, h = m.Hidden ? (bool?) true : null, k = m.Cls,
                g = m.Guids, a = m.Asset, mk = m.MaxKills > 0 ? (int?) m.MaxKills : null,
                by = m.By >= 0 ? (int?) m.By : null, how = m.How, sc = m.Spawn
            })
        };
        File.WriteAllText(Path.Combine(outDir, "markers.json"),
            JsonConvert.SerializeObject(json, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

        var byType = Markers.GroupBy(m => m.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}");
        Console.Error.WriteLine($"markers: {Markers.Count} ({string.Join(", ", byType)}); items: {Items.Count}; enemies: {Enemies.Count}");
    }
}
