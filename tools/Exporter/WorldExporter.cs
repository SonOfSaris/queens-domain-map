using System.Globalization;
using System.Text;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;

namespace QDExport;

public sealed class LevelRef
{
    public string Id;
    public string PackagePath;
    public FTransform Transform = FTransform.Identity;
    public bool InitiallyLoaded;
    public string StreamingClass;
    public LevelGeo Geo;
    public int ActorCount;
    public bool HiddenByDefault;
}

/// <summary>Per-level scene graph helper: resolves component world transforms the way the engine would.</summary>
public sealed class LevelCtx
{
    public LevelRef Ref;
    public IPackage Pkg;
    public List<UObject> Exports = new();
    public List<UObject> Actors = new();
    public readonly Dictionary<UObject, Dictionary<string, UObject>> Children = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<UObject, FTransform> _xf = new(ReferenceEqualityComparer.Instance);

    public UObject Child(UObject owner, string name)
    {
        if (owner == null || name == null) return null;
        if (!Children.TryGetValue(owner, out var map)) return null;
        if (map.TryGetValue(name, out var c)) return c;
        if (name.EndsWith("_GEN_VARIABLE") && map.TryGetValue(name[..^13], out c)) return c;
        return null;
    }

    /// <summary>Map an object reference that may point at an archetype (in a blueprint package) to the instance inside this level.</summary>
    public UObject ToInstance(UObject owner, FPackageIndex idx)
    {
        if (idx == null || idx.IsNull) return null;
        var obj = P.Obj(idx);
        if (obj != null && ReferenceEquals(obj.Owner, Pkg)) return obj;
        string name = null;
        try { name = idx.ResolvedObject?.Name.Text; }
        catch { /* ignored */ }
        return Child(owner, name);
    }

    public UObject Root(UObject actor) => ToInstance(actor, P.Get<FPackageIndex>(actor, "RootComponent"));

    public UObject AttachParent(UObject comp)
    {
        var owner = P.Outer(comp);
        var depth = 0;
        for (var o = comp; o != null && depth++ < 32; o = P.Tmpl(o))
        {
            foreach (var p in o.Properties)
            {
                if (p.Name.Text != "AttachParent") continue;
                if (p.Tag?.GetValue(typeof(FPackageIndex)) is not FPackageIndex idx || idx.IsNull) return null;
                if (ReferenceEquals(o, comp))
                {
                    var direct = P.Obj(idx);
                    if (direct != null) return direct;
                }
                return ToInstance(owner, idx);
            }
        }
        // construction-script components without an explicit parent hang off the actor root
        var root = owner == null ? null : Root(owner);
        return root != null && !ReferenceEquals(root, comp) ? root : null;
    }

    public FTransform WorldXf(UObject comp, int depth = 0)
    {
        if (comp == null) return Ref.Transform;
        if (_xf.TryGetValue(comp, out var cached)) return cached;
        var loc = P.Get(comp, "RelativeLocation", FVector.ZeroVector);
        var rot = P.Get(comp, "RelativeRotation", FRotator.ZeroRotator);
        var scl = P.Get(comp, "RelativeScale3D", FVector.OneVector);
        var rel = new FTransform(rot, loc, scl);
        FTransform world;
        var parent = depth < 64 ? AttachParent(comp) : null;
        if (parent == null)
        {
            world = rel * Ref.Transform;
        }
        else
        {
            var pw = WorldXf(parent, depth + 1);
            world = rel * pw;
            var absL = P.Get(comp, "bAbsoluteLocation", false);
            var absR = P.Get(comp, "bAbsoluteRotation", false);
            var absS = P.Get(comp, "bAbsoluteScale", false);
            if (absL || absR || absS)
            {
                var alone = rel * Ref.Transform;
                if (absL) world.Translation = alone.Translation;
                if (absR) world.Rotation = alone.Rotation;
                if (absS) world.Scale3D = alone.Scale3D;
            }
        }
        _xf[comp] = world;
        return world;
    }
}

public static class WorldExporter
{
    public const string PersistentMap = "QueensDomain/Content/RuinsLevel/Maps/Ruins_Level_00";
    public static readonly MaterialLibrary Mats = new();
    public static readonly MeshLibrary Meshes = new();
    private static readonly List<string> Big = new();
    private static readonly Dictionary<string, int> MissingMeshes = new();
    private static readonly List<(float x, float y, float z, float r)> Trace = new();
    private static readonly Dictionary<string, (long tris, HashSet<string> mats, int n)> ClassGeo = new();
    private static readonly Dictionary<string, ActorStat> ActorStats = new();

    private sealed class ActorStat
    {
        public int Count, WithItems, WithGuids, Smc;
        public string Chain;
        public readonly HashSet<string> Levels = new();
        public readonly HashSet<string> Props = new();
    }

    public static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static int Run(string[] a)
    {
        var outDir = a[0];
        var only = a.Skip(1).FirstOrDefault(x => x.StartsWith("--levels="))?[9..].Split(',');
        var noTex = a.Contains("--no-tex");
        var noGeo = a.Contains("--no-geo");
        var tr = a.Skip(1).FirstOrDefault(x => x.StartsWith("--trace="))?[8..];
        if (tr != null)
            foreach (var part in tr.Split(';'))
            {
                var f = part.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                Trace.Add((f[0], f[1], f[2], f.Length > 3 ? f[3] : 3f));
            }
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "geo"));
        Directory.CreateDirectory(Path.Combine(outDir, "_survey"));

        var levels = DiscoverLevels();
        var exclude = new[] { "997_FinalZoneCutscene", "998_QueenCutscenes", "999_SipehaCutscenes", "060_Ocean_Shadow", "000_Shared_Base_Lighting" };
        levels = levels.Where(l => !exclude.Contains(l.Id)).ToList();
        foreach (var l in levels) l.HiddenByDefault = l.Id.StartsWith("Intro_Level");
        if (only != null) levels = levels.Where(l => only.Any(o => l.Id.Contains(o, StringComparison.OrdinalIgnoreCase))).ToList();
        Console.Error.WriteLine($"levels: {levels.Count}");

        var markers = new MarkerCollector();
        var zone = 0;
        foreach (var lr in levels)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Progress.Report(PZones.From + PZones.Span * zone / levels.Count, $"Zone {++zone} of {levels.Count}: {lr.Id}");
            try
            {
                var ctx = LoadLevel(lr);
                ProcessGeometry(ctx);
                SurveyActors(ctx);
                markers.Collect(ctx);
                lr.Geo.ClipWater(60f);
                if (!noGeo) lr.Geo.Write(Path.Combine(outDir, "geo", lr.Id + ".bin"));
                Console.Error.WriteLine($"  {lr.Id,-32} actors={lr.ActorCount,6} inst={lr.Geo.Instances,6} verts={lr.Geo.Verts,9} tris={lr.Geo.Tris,9} groups={lr.Geo.Groups.Count,4}  {sw.ElapsedMilliseconds} ms");
                // free the big buffers
                foreach (var g in lr.Geo.Groups.Values) { g.Pos.Clear(); g.Pos.TrimExcess(); g.Nrm.Clear(); g.Nrm.TrimExcess(); g.UV.Clear(); g.UV.TrimExcess(); g.Idx.Clear(); g.Idx.TrimExcess(); }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  {lr.Id}: FAILED {e}");
            }
        }

        if (!noTex) Mats.ExportTextures(Path.Combine(outDir, "tex"), 512);
        Progress.Report(PItems.From, "Items, icons and where everything comes from");
        markers.Finish(outDir, noTex);

        Progress.Report(PItems.From + PItems.Span, "Writing the zone list");
        WriteWorld(outDir, levels, markers);
        WriteSurvey(Path.Combine(outDir, "_survey"), levels);
        Stats.Write(outDir);
        Progress.Report(1, "Done");
        return 0;
    }

    // Shares of the whole export as measured on the Nov 2026 build (about half a minute in all): zones, textures, items.
    public static readonly (double From, double Span) PZones = (0.03, 0.48), PTextures = (0.51, 0.09), PItems = (0.60, 0.37);

    public static List<LevelRef> DiscoverLevels()
    {
        var list = new List<LevelRef>();
        var pkg = Program.Provider.LoadPackage(PersistentMap + ".umap");
        var world = pkg.GetExports().OfType<UWorld>().First();
        list.Add(new LevelRef { Id = "Ruins_Level_00", PackagePath = PersistentMap + ".umap", InitiallyLoaded = true, StreamingClass = "Persistent" });
        foreach (var idx in world.StreamingLevels ?? Array.Empty<FPackageIndex>())
        {
            if (P.Obj(idx) is not ULevelStreaming ls || ls.WorldAsset is not { } asset) continue;
            var full = asset.AssetPathName.Text; // /Game/RuinsLevel/Maps/100_GR.100_GR
            var pk = full.Split('.')[0];
            if (!pk.StartsWith("/Game/")) continue;
            var path = "QueensDomain/Content/" + pk[6..] + ".umap";
            var lr = new LevelRef
            {
                Id = pk[(pk.LastIndexOf('/') + 1)..],
                PackagePath = path,
                InitiallyLoaded = P.Get(ls, "bInitiallyLoaded", false),
                StreamingClass = ls.ExportType
            };
            if (ls.LevelTransform is { } t)
            {
                if (t.Rotation.X == 0 && t.Rotation.Y == 0 && t.Rotation.Z == 0 && t.Rotation.W == 0) t.Rotation = FQuat.Identity;
                if (t.Scale3D.X == 0 && t.Scale3D.Y == 0 && t.Scale3D.Z == 0) t.Scale3D = FVector.OneVector;
                lr.Transform = t;
            }
            list.Add(lr);
        }
        return list;
    }

    public static LevelCtx LoadLevel(LevelRef lr)
    {
        var pkg = Program.Provider.LoadPackage(lr.PackagePath);
        var ctx = new LevelCtx { Ref = lr, Pkg = pkg };
        var failed = 0;
        for (var i = 0; i < pkg.ExportsLazy.Length; i++)
        {
            try
            {
                var o = pkg.ExportsLazy[i].Value;
                if (o != null) ctx.Exports.Add(o);
            }
            catch { failed++; }
        }
        if (failed > 0) Console.Error.WriteLine($"  {lr.Id}: {failed} exports failed to load");

        foreach (var e in ctx.Exports)
        {
            var outer = P.Outer(e);
            if (outer == null) continue;
            if (!ctx.Children.TryGetValue(outer, out var map)) ctx.Children[outer] = map = new Dictionary<string, UObject>();
            map[e.Name] = e;
        }

        var level = ctx.Exports.OfType<ULevel>().FirstOrDefault();
        if (level != null)
        {
            foreach (var ai in level.Actors)
            {
                var actor = P.Obj(ai);
                if (actor != null) ctx.Actors.Add(actor);
            }
        }
        lr.ActorCount = ctx.Actors.Count;
        lr.Geo = new LevelGeo();
        return ctx;
    }

    private static void ProcessGeometry(LevelCtx ctx)
    {
        var geo = ctx.Ref.Geo;
        foreach (var e in ctx.Exports)
        {
            if (e is not UStaticMeshComponent smc) continue;
            var owner = P.Outer(smc);
            if (owner == null) continue;
            string hiddenWhy = null;
            if (!P.Get(smc, "bVisible", true)) hiddenWhy = "bVisible=false";
            else if (P.Get(smc, "bHiddenInGame", false)) hiddenWhy = "bHiddenInGame";
            else if (P.Get(owner, "bHidden", false)) hiddenWhy = "actor bHidden";
            if (hiddenWhy != null && Trace.Count == 0) continue;

            var meshIdx = P.Get<FPackageIndex>(smc, "StaticMesh");
            var mesh = Meshes.Get(meshIdx);
            if (mesh == null)
            {
                if (meshIdx is { IsNull: false })
                {
                    var mn = P.PathOf(meshIdx) ?? "?";
                    MissingMeshes[mn] = MissingMeshes.GetValueOrDefault(mn) + 1;
                }
                continue;
            }
            if (hiddenWhy != null)
            {
                // trace mode only: report hidden components near the traced points, never draw them
                var hw = ctx.WorldXf(smc);
                var hc = hw.TransformPosition(mesh.Origin);
                var hs = MathF.Max(MathF.Abs(hw.Scale3D.X), MathF.Max(MathF.Abs(hw.Scale3D.Y), MathF.Abs(hw.Scale3D.Z)));
                float hx = hc.X * 0.01f, hy = hc.Z * 0.01f, hz = hc.Y * 0.01f, hr = mesh.Radius * hs * 0.01f;
                foreach (var t in Trace)
                {
                    var d = MathF.Sqrt((hx - t.x) * (hx - t.x) + (hy - t.y) * (hy - t.y) + (hz - t.z) * (hz - t.z));
                    if (d <= hr + t.r && hr <= 400)
                        Console.Error.WriteLine($"TRACE ({t.x},{t.y},{t.z}) d={d:0.0} rad={hr:0.0} HIDDEN[{hiddenWhy}] {ctx.Ref.Id} {owner.ExportType} {owner.Name}.{smc.Name} mesh={mesh.Name} tris={mesh.Tris}");
                }
                continue;
            }

            var overrides = P.Get<FPackageIndex[]>(smc, "OverrideMaterials");
            MatInfo MatFor(int slot)
            {
                FPackageIndex m = null;
                if (overrides != null && slot < overrides.Length && overrides[slot] != null && !overrides[slot].IsNull) m = overrides[slot];
                else if (slot < mesh.Mats.Length) m = mesh.Mats[slot];
                return Mats.Resolve(m);
            }

            {
                var before = geo.Tris;
                var key = owner.ExportType;
                if (!ClassGeo.TryGetValue(key, out var cg)) cg = (0, new HashSet<string>(), 0);
                for (var sl = 0; sl < mesh.Mats.Length && cg.mats.Count < 12; sl++) { var mm = MatFor(sl); if (mm != null && !mm.Skip) cg.mats.Add(mm.Name); }
                ClassGeo[key] = (cg.tris + mesh.Tris, cg.mats, cg.n + 1);
            }
            var world = ctx.WorldXf(smc);
            var maxScale = MathF.Max(MathF.Abs(world.Scale3D.X), MathF.Max(MathF.Abs(world.Scale3D.Y), MathF.Abs(world.Scale3D.Z)));

            if (Trace.Count > 0)
            {
                var c = world.TransformPosition(mesh.Origin);
                float cx = c.X * 0.01f, cy = c.Z * 0.01f, cz = c.Y * 0.01f, rad = mesh.Radius * maxScale * 0.01f;
                foreach (var t in Trace)
                {
                    var d = MathF.Sqrt((cx - t.x) * (cx - t.x) + (cy - t.y) * (cy - t.y) + (cz - t.z) * (cz - t.z));
                    if (d > rad + t.r || rad > 400) continue;
                    var mats = string.Join(",", Enumerable.Range(0, mesh.Mats.Length).Select(sl => { var mm = MatFor(sl); return mm == null ? "?" : mm.Name + (mm.Skip ? "(skip)" : "") + "[" + mm.Tex?.Name + "]"; }).Distinct());
                    Console.Error.WriteLine($"TRACE ({t.x},{t.y},{t.z}) d={d:0.0} rad={rad:0.0} {ctx.Ref.Id} {owner.ExportType} {owner.Name}.{smc.Name} mesh={mesh.Name} tris={mesh.Tris} mats={mats}");
                }
            }

            if (smc is UInstancedStaticMeshComponent ism)
            {
                foreach (var inst in ism.PerInstanceSMData ?? Array.Empty<FInstancedStaticMeshInstanceData>())
                    geo.Emit(mesh, MatFor, Affine.From(inst.TransformData * world));
            }
            else if (smc is USplineMeshComponent)
            {
                geo.Emit(mesh, MatFor, Affine.From(world), new SplineDeformer(smc, mesh));
            }
            else
            {
                geo.Emit(mesh, MatFor, Affine.From(world));
                var radius = mesh.Radius * maxScale * LevelGeo.Scale;
                if (radius > 150)
                {
                    var c = world.TransformPosition(mesh.Origin);
                    var mats = string.Join(",", Enumerable.Range(0, mesh.Mats.Length).Select(s => MatFor(s)?.Name).Distinct());
                    Big.Add($"{ctx.Ref.Id}\t{owner.ExportType}\t{owner.Name}\t{mesh.Name}\t{F(radius)}\t{F(c.X * 0.01f)},{F(c.Y * 0.01f)},{F(c.Z * 0.01f)}\t{mesh.Tris}\t{mats}");
                }
            }
        }
    }

    private static void SurveyActors(LevelCtx ctx)
    {
        foreach (var actor in ctx.Actors)
        {
            var cls = actor.ExportType;
            if (!ActorStats.TryGetValue(cls, out var st))
                ActorStats[cls] = st = new ActorStat { Chain = string.Join(" < ", P.ClassChain(actor)) };
            st.Count++;
            st.Levels.Add(ctx.Ref.Id);
            if (P.FindTag(actor, "ItemsToGrant") != null) st.WithItems++;
            if (P.FindTag(actor, "GUIDs") != null) st.WithGuids++;
            if (ctx.Children.TryGetValue(actor, out var kids)) st.Smc += kids.Values.Count(k => k is UStaticMeshComponent);
            foreach (var p in actor.Properties)
            {
                if (st.Props.Count >= 60) break;
                // skip plain references to the actor's own components
                if (p.Tag?.GenericValue is FPackageIndex pi && P.Obj(pi) is { } po && ReferenceEquals(P.Outer(po), actor)) continue;
                st.Props.Add(p.Name.Text);
            }
        }
    }

    private static void WriteWorld(string outDir, List<LevelRef> levels, MarkerCollector markers)
    {
        string Region(string id) => markers.LevelRegions.TryGetValue(id, out var rc) ? rc.OrderByDescending(k => k.Value).First().Key : null;
        var world = new
        {
            generated = DateTime.UtcNow.ToString("u"),
            scale = LevelGeo.Scale,
            levels = levels.Where(l => l.Geo != null).Select(l => new
            {
                id = l.Id,
                file = "geo/" + l.Id + ".bin",
                region = Region(l.Id),
                hidden = l.HiddenByDefault,
                initiallyLoaded = l.InitiallyLoaded,
                streaming = l.StreamingClass,
                min = l.Geo.Tris > 0 ? (l.Geo.HasSolid ? l.Geo.SolidMin : l.Geo.Min) : null,
                max = l.Geo.Tris > 0 ? (l.Geo.HasSolid ? l.Geo.SolidMax : l.Geo.Max) : null,
                note = l.Id.StartsWith("Intro_Level") ? "intro cutscene set (re-uses world pieces at other coordinates)" : null,
                tris = l.Geo.Tris,
                verts = l.Geo.Verts,
                actors = l.ActorCount
            }),
            textures = Mats.Textures.Select(t => new { id = t.Id, name = t.Name, file = t.Ok ? "tex/" + t.File : null, w = t.W, h = t.H, alpha = t.HasAlpha, nearest = t.Nearest, avg = t.Avg.Select(v => MathF.Round(v, 3)) }),
            states = Mats.States.Select(s => new { id = s.Id, tex = s.Tex, blend = s.Blend, water = s.Water, twoSided = s.TwoSided, clip = MathF.Round(s.Clip, 3), color = s.Color?.Select(v => MathF.Round(v, 3)) })
        };
        File.WriteAllText(Path.Combine(outDir, "world.json"), JsonConvert.SerializeObject(world, Formatting.None));
    }

    private static void WriteSurvey(string dir, List<LevelRef> levels)
    {
        var sb = new StringBuilder("class\tcount\tlevels\twithItems\twithGuids\tsmc\tchain\tprops\n");
        foreach (var (cls, st) in ActorStats.OrderByDescending(k => k.Value.Count))
            sb.Append($"{cls}\t{st.Count}\t{st.Levels.Count}\t{st.WithItems}\t{st.WithGuids}\t{st.Smc}\t{st.Chain}\t{string.Join(",", st.Props)}\n");
        File.WriteAllText(Path.Combine(dir, "actors.tsv"), sb.ToString());

        sb = new StringBuilder("name\ttris\tblend\ttwoSided\tskip\ttex\ttexParam\tchain\tcandidates\n");
        foreach (var m in Mats.ByPath.Values.OrderByDescending(m => m.Tris))
            sb.Append($"{m.Name}\t{m.Tris}\t{m.Blend}\t{m.TwoSided}\t{m.SkipWhy}\t{m.Tex?.Name}\t{m.TexParam}\t{m.Chain}\t{m.Candidates}\n");
        File.WriteAllText(Path.Combine(dir, "materials.tsv"), sb.ToString());

        sb = new StringBuilder("name\tverts\ttris\tinstances\tradius_m\tpath\n");
        foreach (var m in Meshes.All.OrderByDescending(m => (long) m.Tris * m.Instances))
            sb.Append($"{m.Name}\t{m.Verts}\t{m.Tris}\t{m.Instances}\t{F(m.Radius * 0.01f)}\t{m.Path}\n");
        File.WriteAllText(Path.Combine(dir, "meshes.tsv"), sb.ToString());

        sb = new StringBuilder("level\tactorClass\tactor\tmesh\tradius_m\tcenter_ue_m\ttris\tmaterials\n");
        foreach (var b in Big) sb.Append(b).Append('\n');
        File.WriteAllText(Path.Combine(dir, "big.tsv"), sb.ToString());

        sb = new StringBuilder("mesh\tcomponents\n");
        foreach (var (mesh, n) in MissingMeshes.OrderByDescending(k => k.Value)) sb.Append($"{mesh}\t{n}\n");
        File.WriteAllText(Path.Combine(dir, "missing_meshes.tsv"), sb.ToString());
        if (MissingMeshes.Count > 0) Console.Error.WriteLine($"meshes that could not be read: {MissingMeshes.Count} ({MissingMeshes.Values.Sum()} components) - see _survey/missing_meshes.tsv");

        sb = new StringBuilder("class\tcomponents\ttris\tmaterials\n");
        foreach (var (cls, cg) in ClassGeo.OrderByDescending(k => k.Value.tris))
            sb.Append($"{cls}\t{cg.n}\t{cg.tris}\t{string.Join(",", cg.mats)}\n");
        File.WriteAllText(Path.Combine(dir, "classgeo.tsv"), sb.ToString());

        sb = new StringBuilder("level\tactors\tinstances\tverts\ttris\tgroups\tmin\tmax\n");
        foreach (var l in levels.Where(l => l.Geo != null))
            sb.Append($"{l.Id}\t{l.ActorCount}\t{l.Geo.Instances}\t{l.Geo.Verts}\t{l.Geo.Tris}\t{l.Geo.Groups.Count}\t{string.Join(",", l.Geo.Min.Select(F))}\t{string.Join(",", l.Geo.Max.Select(F))}\n");
        File.WriteAllText(Path.Combine(dir, "levels.tsv"), sb.ToString());
    }
}
