using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

namespace QDExport;

/// <summary>File provider that keeps every loaded package alive, so imports resolve to stable object instances.</summary>
public sealed class CachingProvider : DefaultFileProvider
{
    private readonly Dictionary<string, IPackage> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CachingProvider(string dir, VersionContainer v)
        : base(dir, SearchOption.TopDirectoryOnly, v, StringComparer.OrdinalIgnoreCase) { }

    public override IPackage LoadPackage(GameFile file)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(file.Path, out var p)) return p;
        }
        var pkg = base.LoadPackage(file);
        lock (_cache)
        {
            if (_cache.TryGetValue(file.Path, out var p)) return p;
            _cache[file.Path] = pkg;
        }
        return pkg;
    }
}

/// <summary>How far the export is, for the program that started it (App.Build reads these lines and shows them on the page).</summary>
public static class Progress
{
    public static void Report(double fraction, string text) =>
        Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"@@ {fraction:0.000} {text}"));
}

/// <summary>Property access that falls back through the archetype (template) chain, like UE does at runtime.</summary>
public static class P
{
    public static UObject Tmpl(UObject o)
    {
        try { return o.Template?.Object?.Value; }
        catch { return null; }
    }

    public static FPropertyTag FindTag(UObject obj, string name)
    {
        var depth = 0;
        for (var o = obj; o != null && depth++ < 32; o = Tmpl(o))
        {
            var props = o.Properties;
            for (var i = 0; i < props.Count; i++)
            {
                var p = props[i];
                if (p.ArrayIndex == 0 && p.Name.Text == name) return p;
            }
        }
        return null;
    }

    public static bool TryGet<T>(UObject obj, string name, out T value)
    {
        var tag = FindTag(obj, name);
        if (tag?.Tag?.GetValue(typeof(T)) is T t)
        {
            value = t;
            return true;
        }
        value = default;
        return false;
    }

    public static T Get<T>(UObject obj, string name, T def = default) => TryGet<T>(obj, name, out var v) ? v : def;

    public static bool HasOwn(UObject obj, string name)
    {
        foreach (var p in obj.Properties)
            if (p.Name.Text == name) return true;
        return false;
    }

    public static UObject Obj(FPackageIndex idx)
    {
        try { return idx is null || idx.IsNull ? null : idx.ResolvedObject?.Object?.Value; }
        catch { return null; }
    }

    public static string PathOf(FPackageIndex idx)
    {
        try { return idx is null || idx.IsNull ? null : idx.ResolvedObject?.GetPathName(); }
        catch { return null; }
    }

    /// <summary>Object reference out of a raw property value (plain object or interface reference).</summary>
    public static FPackageIndex AsIndex(object v) => v switch
    {
        FPackageIndex pi => pi,
        FScriptInterface si => si.Object,
        _ => null
    };

    /// <summary>All object references held by a property (single reference or array of references).</summary>
    public static List<FPackageIndex> Refs(UObject obj, string name)
    {
        var list = new List<FPackageIndex>();
        var gv = FindTag(obj, name)?.Tag?.GenericValue;
        if (gv is UScriptArray arr)
        {
            foreach (var el in arr.Properties)
                if (AsIndex(el?.GenericValue) is { IsNull: false } pi) list.Add(pi);
        }
        else if (AsIndex(gv) is { IsNull: false } single) list.Add(single);
        return list;
    }

    public static UObject Outer(UObject o)
    {
        try { return o.Outer?.Object?.Value; }
        catch { return null; }
    }

    private static readonly Dictionary<string, string[]> ChainCache = new();

    /// <summary>Class name followed by its super classes (blueprint parents first, then the first native class).</summary>
    public static string[] ClassChain(UObject o)
    {
        var c = o.Class;
        if (c == null) return Array.Empty<string>();
        var key = c.Name.Text;
        if (ChainCache.TryGetValue(key, out var chain)) return chain;
        var list = new List<string>();
        var depth = 0;
        while (c != null && depth++ < 32)
        {
            list.Add(c.Name.Text);
            try { c = c.Super; }
            catch { c = null; }
        }
        return ChainCache[key] = list.ToArray();
    }

    public static string Safe(string s)
    {
        var chars = s.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray();
        return new string(chars);
    }
}
