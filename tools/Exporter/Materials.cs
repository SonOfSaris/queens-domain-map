using System.Collections.Concurrent;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

namespace QDExport;

public sealed class TexInfo
{
    public int Id;
    public string Path;
    public string Name;
    public UTexture2D Tex;
    public string File;
    public int W, H;
    public bool HasAlpha;
    public bool Nearest;
    public float[] Avg = { 0.5f, 0.5f, 0.5f };
    public bool Ok;
}

public sealed class MatInfo
{
    public string Path;
    public string Name;
    public string Chain;      // parent chain names, leaf first
    public int Blend;         // 0 opaque, 1 masked, 2 translucent, 3 additive, 4 modulate
    public bool TwoSided;
    public float Clip = 0.333f;
    public TexInfo Tex;
    public string TexParam;
    public float[] Color;     // fallback colour when there is no texture
    public bool Skip;
    public bool Water;
    public string SkipWhy;
    public int State = -1;
    public long Tris;         // usage counter (survey)
    public string Candidates; // debug: scored texture candidates
}

public sealed class RenderState
{
    public int Id;
    public int Tex;       // texture id or -1
    public int Blend;
    public bool TwoSided;
    public float Clip;
    public float[] Color;
    public bool Water;
}

public sealed class MaterialLibrary
{
    public readonly List<TexInfo> Textures = new();
    public readonly List<RenderState> States = new();
    public readonly Dictionary<string, MatInfo> ByPath = new();

    private readonly Dictionary<string, TexInfo> _texByPath = new();
    private readonly Dictionary<string, RenderState> _stateByKey = new();
    private readonly Dictionary<UObject, MatInfo> _byObj = new(ReferenceEqualityComparer.Instance);
    private MatInfo _default;

    private static readonly string[] GoodParam = { "texture_main", "diffuse", "albedo", "basecolor", "base color", "base_color", "maintex", "main", "colormap", "color map", "texture", "tex", "colour", "color" };
    private static readonly string[] BadParam = { "normal", "nrm", "mask", "rough", "spec", "metal", "emis", "glow", "noise", "ao", "height", "lut", "detail", "distort", "flow", "opacity", "alpha", "ramp", "gradient", "shadow", "dither", "fog", "cube", "reflect", "blend", "alternate", "trim", "main2", "lightsource", "pallette", "palette", "bayer", "wind", "wpo", "damage", "dissolve", "scroll", "pan", "caustic", "dirt", "grunge" };
    private static readonly string[] BadTexName = { "normal", "_n.", "_nrm", "mask", "noise", "bayer", "blendfunc", "lut", "gradient", "ramp", "foglight", "cubemap", "default", "pallette", "palette", "flowmap", "caustic", "perlin", "cloud" };

    // Materials that never represent visible world geometry.
    public static string[] SkipMatNames = { "fogbox", "doorfog", "characterstuckfixer", "invisible", "nodraw", "postprocess", "wanderradius", "visualizer", "sky", "fogcard", "worldgridmaterial", "m_blackbackground", "bottofocean", "checkpointflash", "swamp3dbubble", "solidunlit", "seasipeharune" };
    private static readonly string[] WaterNames = { "water", "ocean" };
    // translucent materials that are visual effects rather than world surfaces
    private static readonly string[] FxNames = { "particle", "flare", "glow", "godray", "god_ray", "godspline", "godparticle", "beam", "dust", "flake", "cinder", "ghost", "areafx", "elec", "_fx", "fx_", "shield", "ripple", "bubble", "steam", "smoke", "stink", "incense", "flame", "fire", "cloud", "runevision", "whirlpool", "wake", "mandala", "cosmi", "energyblade", "ethereal", "wings", "emis", "aurora", "spark", "trail", "fog", "caustic", "shimmer", "lightshaft", "blooddome" };

    public MatInfo Resolve(FPackageIndex idx)
    {
        var obj = P.Obj(idx);
        if (obj == null) return Default();
        if (_byObj.TryGetValue(obj, out var mi)) return mi;
        mi = Build(obj, idx);
        _byObj[obj] = mi;
        if (mi.Path != null) ByPath[mi.Path] = mi;
        return mi;
    }

    public MatInfo Default()
    {
        if (_default != null) return _default;
        _default = new MatInfo { Path = "<default>", Name = "<default>", Chain = "", Color = new[] { 0.55f, 0.55f, 0.55f } };
        AssignState(_default);
        ByPath[_default.Path] = _default;
        return _default;
    }

    private MatInfo Build(UObject leaf, FPackageIndex idx)
    {
        var mi = new MatInfo { Path = P.PathOf(idx) ?? leaf.Name, Name = leaf.Name };
        var chain = new List<UObject>();
        for (var o = leaf; o != null && chain.Count < 16;)
        {
            chain.Add(o);
            o = o is UMaterialInstance inst ? P.Obj(inst.Parent) : null;
        }
        mi.Chain = string.Join(" < ", chain.Select(c => c.Name));

        // ---- blend / two sided / clip: leaf-most override wins, else base material ----
        var baseMat = chain[^1] as UMaterial;
        EBlendMode blend = baseMat?.BlendMode ?? EBlendMode.BLEND_Opaque;
        var twoSided = baseMat?.TwoSided ?? false;
        var clip = baseMat?.OpacityMaskClipValue ?? 0.333f;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i] is not UMaterialInstance inst || inst.BasePropertyOverrides is not { } ov) continue;
            if (ov.bOverride_BlendMode) blend = ov.BlendMode;
            if (ov.bOverride_TwoSided) twoSided = ov.TwoSided;
            if (ov.bOverride_OpacityMaskClipValue) clip = ov.OpacityMaskClipValue;
        }
        mi.Blend = blend switch
        {
            EBlendMode.BLEND_Opaque => 0,
            EBlendMode.BLEND_Masked => 1,
            EBlendMode.BLEND_Translucent => 2,
            EBlendMode.BLEND_Additive => 3,
            EBlendMode.BLEND_Modulate => 4,
            _ => 2
        };
        mi.TwoSided = twoSided;
        mi.Clip = clip;

        // ---- texture candidates: parameters along the chain (leaf overrides parent), then base material defaults ----
        var cands = new Dictionary<string, FPackageIndex>(StringComparer.OrdinalIgnoreCase);
        var colors = new Dictionary<string, FLinearColor>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in chain)
        {
            if (o is UMaterialInstanceConstant mic)
            {
                foreach (var tp in mic.TextureParameterValues)
                    if (tp.ParameterValue != null && !tp.ParameterValue.IsNull && !cands.ContainsKey(tp.Name))
                        cands[tp.Name] = tp.ParameterValue;
                foreach (var vp in mic.VectorParameterValues)
                    if (vp.ParameterValue != null && !colors.ContainsKey(vp.Name))
                        colors[vp.Name] = vp.ParameterValue.Value;
            }
        }
        if (baseMat != null)
        {
            // named parameter defaults stored in the cooked material
            try
            {
                if (baseMat.CachedExpressionData is { } ced && ced.TryGetValue(out FStructFallback pars, "Parameters"))
                {
                    FStructFallback texEntry = null;
                    foreach (var p in pars.Properties)
                        if (p.Name.Text == "RuntimeEntries" && p.ArrayIndex == 2)
                            texEntry = p.Tag?.GetValue(typeof(FStructFallback)) as FStructFallback;
                    if (texEntry != null && texEntry.TryGetValue(out FStructFallback[] infos, "ParameterInfos") &&
                        pars.TryGetValue(out FPackageIndex[] texVals, "TextureValues"))
                    {
                        for (var i = 0; i < infos.Length && i < texVals.Length; i++)
                        {
                            if (!infos[i].TryGetValue(out FName nm, "Name")) continue;
                            if (texVals[i] != null && !texVals[i].IsNull && !cands.ContainsKey(nm.Text))
                                cands[nm.Text] = texVals[i];
                        }
                    }
                }
            }
            catch { /* fall through to ReferencedTextures */ }

            var n = 0;
            foreach (var rt in baseMat.ReferencedTextures)
            {
                if (rt == null || rt.IsNull) continue;
                var key = "<ref" + n++ + ">";
                if (!cands.Values.Any(v => ReferenceEquals(P.Obj(v), P.Obj(rt))))
                    cands[key] = rt;
            }
        }

        UTexture2D best = null;
        string bestParam = null;
        var bestScore = int.MinValue;
        var dbg = new List<string>();
        foreach (var (param, tidx) in cands)
        {
            if (P.Obj(tidx) is not UTexture2D tex) continue;
            var s = Score(param, tex);
            dbg.Add($"{param}={tex.Name}:{s}");
            if (s > bestScore)
            {
                bestScore = s;
                best = tex;
                bestParam = param;
            }
        }
        mi.Candidates = string.Join(", ", dbg);
        if (best != null && bestScore > -30)
        {
            mi.Tex = GetTexture(best);
            mi.TexParam = bestParam;
        }

        // ---- fallback colour ----
        foreach (var name in new[] { "Color", "BaseColor", "Base Color", "Tint", "Colour", "MainColor", "Main_Color", "DiffuseColor", "Diffuse" })
        {
            if (!colors.TryGetValue(name, out var c)) continue;
            mi.Color = new[] { Math.Clamp(c.R, 0, 1), Math.Clamp(c.G, 0, 1), Math.Clamp(c.B, 0, 1) };
            break;
        }

        // ---- skip rules ----
        var lname = (mi.Name + " " + mi.Chain).ToLowerInvariant();
        foreach (var bad in SkipMatNames)
        {
            if (!lname.Contains(bad)) continue;
            mi.Skip = true;
            mi.SkipWhy = "name:" + bad;
            break;
        }
        if (!mi.Skip && mi.Blend == 3)
        {
            mi.Skip = true;
            mi.SkipWhy = "additive";
        }
        if (!mi.Skip && mi.Blend is 2 or 4)
        {
            if (WaterNames.Any(lname.Contains)) mi.Water = true;
            else if (FxNames.FirstOrDefault(lname.Contains) is { } fx)
            {
                mi.Skip = true;
                mi.SkipWhy = "fx:" + fx;
            }
        }
        if (!mi.Skip && mi.Tex == null && mi.Name == "Empty")
        {
            mi.Skip = true;
            mi.SkipWhy = "empty";
        }
        if (!mi.Skip && baseMat != null && baseMat.MaterialDomain != EMaterialDomain.MD_Surface)
        {
            mi.Skip = true;
            mi.SkipWhy = "domain:" + baseMat.MaterialDomain;
        }

        AssignState(mi);
        return mi;
    }

    private static int Score(string param, UTexture2D tex)
    {
        var s = 0;
        var p = param.ToLowerInvariant();
        var t = tex.Name.ToLowerInvariant() + ".";
        if (p.StartsWith("<ref")) s -= 10;
        if (p == "texture_main") s += 100;
        foreach (var g in GoodParam)
            if (p.Contains(g)) { s += 40; break; }
        foreach (var b in BadParam)
            if (p.Contains(b)) { s -= 50; break; }
        foreach (var b in BadTexName)
            if (t.Contains(b)) { s -= 45; break; }
        if (tex.IsNormalMap) s -= 200;
        if (!tex.SRGB) s -= 25;
        return s;
    }

    public TexInfo GetTexture(UTexture2D tex)
    {
        var path = tex.GetPathName();
        if (_texByPath.TryGetValue(path, out var ti)) return ti;
        ti = new TexInfo { Id = Textures.Count, Path = path, Name = tex.Name, Tex = tex, Nearest = tex.RenderNearestNeighbor };
        Textures.Add(ti);
        _texByPath[path] = ti;
        return ti;
    }

    private void AssignState(MatInfo mi)
    {
        if (mi.Skip) return;
        var color = mi.Tex == null ? mi.Color ?? new[] { 0.55f, 0.55f, 0.55f } : null;
        var key = $"{mi.Tex?.Id ?? -1}|{mi.Blend}|{(mi.TwoSided ? 1 : 0)}|{(mi.Blend == 1 ? MathF.Round(mi.Clip, 2) : 0)}|" +
                  (color == null ? "" : $"{(int) (color[0] * 255)},{(int) (color[1] * 255)},{(int) (color[2] * 255)}") + (mi.Water ? "|w" : "");
        if (!_stateByKey.TryGetValue(key, out var st))
        {
            st = new RenderState { Id = States.Count, Tex = mi.Tex?.Id ?? -1, Blend = mi.Blend, TwoSided = mi.TwoSided, Clip = mi.Clip, Color = color, Water = mi.Water };
            States.Add(st);
            _stateByKey[key] = st;
        }
        mi.State = st.Id;
    }

    /// <summary>Decode and write every referenced texture as PNG (parallel).</summary>
    public void ExportTextures(string dir, int maxSize)
    {
        Directory.CreateDirectory(dir);
        var options = new ExportOptions();
        var failed = new ConcurrentBag<string>();
        var done = 0;
        var (from, span) = WorldExporter.PTextures;
        Progress.Report(from, $"Textures: 0 of {Textures.Count}");
        Parallel.ForEach(Textures, new ParallelOptions { MaxDegreeOfParallelism = 8 }, ti =>
        {
            var k = Interlocked.Increment(ref done);
            if (k % 20 == 0) Progress.Report(from + span * k / Textures.Count, $"Textures: {k} of {Textures.Count}");
            try
            {
                var ct = ti.Tex.Decode(maxSize, options);
                if (ct == null) { failed.Add(ti.Path + " (null)"); return; }
                using var bmp = ct.ToSkBitmap();
                using var rgba = new SKBitmap(new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                if (!bmp.CopyTo(rgba, SKColorType.Rgba8888)) { failed.Add(ti.Path + " (copy)"); return; }
                var px = rgba.Bytes;
                long r = 0, g = 0, b = 0, n = 0;
                var alpha = false;
                for (var i = 0; i < px.Length; i += 4)
                {
                    var a = px[i + 3];
                    if (a < 250) alpha = true;
                    if (a < 128) continue;
                    r += px[i]; g += px[i + 1]; b += px[i + 2]; n++;
                }
                if (n > 0) ti.Avg = new[] { r / (255f * n), g / (255f * n), b / (255f * n) };
                ti.HasAlpha = alpha;
                ti.W = rgba.Width;
                ti.H = rgba.Height;
                ti.File = $"{ti.Id}.png";
                using var data = rgba.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.Create(Path.Combine(dir, ti.File));
                data.SaveTo(fs);
                ti.Ok = true;
            }
            catch (Exception e)
            {
                failed.Add(ti.Path + " (" + e.GetType().Name + ": " + e.Message + ")");
            }
        });
        foreach (var f in failed) Console.Error.WriteLine("texture failed: " + f);
        Console.Error.WriteLine($"textures: {Textures.Count(t => t.Ok)}/{Textures.Count} exported");
    }
}
