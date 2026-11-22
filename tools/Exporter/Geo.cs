using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace QDExport;

public sealed class MeshSection
{
    public int Slot;
    public int[] VertIdx; // unique vertex indices of the mesh used by this section
    public int[] Tri;     // triangle list, indices into VertIdx
}

public sealed class MeshData
{
    public string Path;
    public string Name;
    public float[] Pos; // xyz, UE local space
    public float[] Nrm; // xyz
    public float[] UV;  // uv (channel 0)
    public MeshSection[] Sections;
    public FPackageIndex[] Mats; // per material slot
    public FVector Origin, Extent;
    public float Radius;
    public int Facing = 1; // +1 when (v1-v0)x(v2-v0) agrees with the vertex normals in local space
    public int Tris, Verts;
    public int Instances; // usage counter (survey)
}

public sealed class MeshLibrary
{
    private readonly Dictionary<UObject, MeshData> _byObj = new(ReferenceEqualityComparer.Instance);
    public IEnumerable<MeshData> All => _byObj.Values.Where(m => m != null);

    public MeshData Get(FPackageIndex idx)
    {
        var obj = P.Obj(idx);
        if (obj is not UStaticMesh sm) return null;
        if (_byObj.TryGetValue(sm, out var md)) return md;
        try { md = Convert(sm, P.PathOf(idx)); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mesh failed: {sm.Name}: {e.Message}");
            md = null;
        }
        _byObj[sm] = md;
        return md;
    }

    private static MeshData Convert(UStaticMesh sm, string path)
    {
        var rd = sm.RenderData;
        if (rd?.LODs == null || rd.LODs.Length == 0) return null;
        FStaticMeshLODResources lod = null;
        foreach (var l in rd.LODs)
        {
            if (l == null || l.SkipLod) continue;
            lod = l;
            break;
        }
        if (lod == null) return null;

        var verts = lod.PositionVertexBuffer!.Verts;
        var uvItems = lod.VertexBuffer!.UV;
        var n = verts.Length;
        var md = new MeshData
        {
            Path = path ?? sm.Name, Name = sm.Name,
            Pos = new float[n * 3], Nrm = new float[n * 3], UV = new float[n * 2],
            Verts = n
        };
        for (var i = 0; i < n; i++)
        {
            var v = verts[i];
            md.Pos[i * 3] = v.X; md.Pos[i * 3 + 1] = v.Y; md.Pos[i * 3 + 2] = v.Z;
            if (i < uvItems.Length)
            {
                var item = uvItems[i];
                var nz = item.Normal[2];
                md.Nrm[i * 3] = nz.X; md.Nrm[i * 3 + 1] = nz.Y; md.Nrm[i * 3 + 2] = nz.Z;
                if (item.UV.Length > 0)
                {
                    md.UV[i * 2] = item.UV[0].U;
                    md.UV[i * 2 + 1] = item.UV[0].V;
                }
            }
        }

        var ib = lod.IndexBuffer!.Buffer!;
        var sections = new List<MeshSection>();
        double facing = 0;
        foreach (var sec in lod.Sections)
        {
            if (sec.NumTriangles <= 0) continue;
            var remap = new Dictionary<int, int>();
            var used = new List<int>();
            var tri = new int[sec.NumTriangles * 3];
            for (var k = 0; k < tri.Length; k++)
            {
                var vi = (int) ib[sec.FirstIndex + k];
                if (!remap.TryGetValue(vi, out var li))
                {
                    li = used.Count;
                    remap[vi] = li;
                    used.Add(vi);
                }
                tri[k] = li;
            }
            for (var k = 0; k < tri.Length; k += 3)
            {
                int a = used[tri[k]], b = used[tri[k + 1]], c = used[tri[k + 2]];
                float ax = md.Pos[a * 3], ay = md.Pos[a * 3 + 1], az = md.Pos[a * 3 + 2];
                float e1x = md.Pos[b * 3] - ax, e1y = md.Pos[b * 3 + 1] - ay, e1z = md.Pos[b * 3 + 2] - az;
                float e2x = md.Pos[c * 3] - ax, e2y = md.Pos[c * 3 + 1] - ay, e2z = md.Pos[c * 3 + 2] - az;
                float cx = e1y * e2z - e1z * e2y, cy = e1z * e2x - e1x * e2z, cz = e1x * e2y - e1y * e2x;
                float nx = md.Nrm[a * 3] + md.Nrm[b * 3] + md.Nrm[c * 3];
                float ny = md.Nrm[a * 3 + 1] + md.Nrm[b * 3 + 1] + md.Nrm[c * 3 + 1];
                float nz = md.Nrm[a * 3 + 2] + md.Nrm[b * 3 + 2] + md.Nrm[c * 3 + 2];
                facing += cx * nx + cy * ny + cz * nz;
            }
            sections.Add(new MeshSection { Slot = sec.MaterialIndex, VertIdx = used.ToArray(), Tri = tri });
            md.Tris += sec.NumTriangles;
        }
        md.Sections = sections.ToArray();
        md.Facing = facing >= 0 ? 1 : -1;

        var mats = sm.StaticMaterials;
        md.Mats = new FPackageIndex[mats?.Length ?? 0];
        for (var i = 0; i < md.Mats.Length; i++) md.Mats[i] = mats[i]?.MaterialInterface;
        if (md.Mats.Length == 0 && sm.Materials != null) md.Mats = sm.Materials.ToArray();

        if (rd.Bounds != null)
        {
            md.Origin = rd.Bounds.Origin;
            md.Extent = rd.Bounds.BoxExtent;
            md.Radius = rd.Bounds.SphereRadius;
        }
        return md;
    }
}

/// <summary>Affine transform flattened to basis vectors (UE space, cm).</summary>
public struct Affine
{
    public FVector Ex, Ey, Ez, T;     // position basis
    public FVector Nx, Ny, Nz;        // normal basis (inverse-scale)
    public float Det;                 // sign of the scale determinant

    public static Affine From(FTransform t)
    {
        var s = t.Scale3D;
        var r = t.Rotation;
        var a = new Affine
        {
            Ex = r.RotateVector(new FVector(s.X, 0, 0)),
            Ey = r.RotateVector(new FVector(0, s.Y, 0)),
            Ez = r.RotateVector(new FVector(0, 0, s.Z)),
            T = t.Translation,
            Nx = r.RotateVector(new FVector(Inv(s.X), 0, 0)),
            Ny = r.RotateVector(new FVector(0, Inv(s.Y), 0)),
            Nz = r.RotateVector(new FVector(0, 0, Inv(s.Z))),
            Det = s.X * s.Y * s.Z < 0 ? -1 : 1
        };
        return a;
    }

    private static float Inv(float v) => MathF.Abs(v) < 1e-8f ? 0 : 1f / v;

    public FVector Pos(float x, float y, float z) => new(
        Ex.X * x + Ey.X * y + Ez.X * z + T.X,
        Ex.Y * x + Ey.Y * y + Ez.Y * z + T.Y,
        Ex.Z * x + Ey.Z * y + Ez.Z * z + T.Z);

    public FVector Normal(float x, float y, float z)
    {
        var nx = Nx.X * x + Ny.X * y + Nz.X * z;
        var ny = Nx.Y * x + Ny.Y * y + Nz.Y * z;
        var nz = Nx.Z * x + Ny.Z * y + Nz.Z * z;
        var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        return len < 1e-12f ? new FVector(0, 0, 1) : new FVector(nx / len, ny / len, nz / len);
    }
}

/// <summary>Port of USplineMeshComponent::CalcSliceTransform: bends a mesh along a cubic hermite spline.</summary>
public sealed class SplineDeformer
{
    private readonly FSplineMeshParams _p;
    private readonly int _axis;
    private readonly FVector _up;
    private readonly float _bMin, _bMax, _meshMin, _meshRange;
    private readonly bool _custom, _smooth;
    public readonly bool Flips;

    public SplineDeformer(UObject comp, MeshData mesh)
    {
        _p = P.Get<FSplineMeshParams>(comp, "SplineParams") ?? new FSplineMeshParams(new CUE4Parse.UE4.Assets.Objects.FStructFallback());
        _axis = (int) P.Get(comp, "ForwardAxis", ESplineMeshAxis.X);
        _up = P.Get(comp, "SplineUpDir", new FVector(0, 0, 1));
        _bMin = P.Get(comp, "SplineBoundaryMin", 0f);
        _bMax = P.Get(comp, "SplineBoundaryMax", 0f);
        _smooth = P.Get(comp, "bSmoothInterpRollScale", false);
        _custom = MathF.Abs(_bMin - _bMax) > 1e-4f;
        float o = _axis == 0 ? mesh.Origin.X : _axis == 1 ? mesh.Origin.Y : mesh.Origin.Z;
        float e = _axis == 0 ? mesh.Extent.X : _axis == 1 ? mesh.Extent.Y : mesh.Extent.Z;
        _meshMin = o - e;
        _meshRange = 2 * e;
        Flips = _p.StartScale.X * _p.StartScale.Y < 0;
    }

    public void Apply(ref float x, ref float y, ref float z, ref float nx, ref float ny, ref float nz)
    {
        var along = _axis == 0 ? x : _axis == 1 ? y : z;
        float alpha = 0;
        if (_custom)
        {
            if (_bMax - _bMin > 0) alpha = (along - _bMin) / (_bMax - _bMin);
        }
        else if (_meshRange > 1e-8f) alpha = (along - _meshMin) / _meshRange;

        var h = _smooth ? alpha * alpha * (3 - 2 * alpha) : alpha;
        if (_smooth) h = Math.Clamp(alpha, 0, 1) is var c0 ? c0 * c0 * (3 - 2 * c0) : h;

        var pos = _p.SplineEvalPos(alpha);
        var dir = _p.SplineEvalDir(alpha);
        var baseX = (_up ^ dir).GetSafeNormal();
        var baseY = (dir ^ baseX).GetSafeNormal();

        var offX = _p.StartOffset.X + (_p.EndOffset.X - _p.StartOffset.X) * h;
        var offY = _p.StartOffset.Y + (_p.EndOffset.Y - _p.StartOffset.Y) * h;
        pos += baseX * offX + baseY * offY;

        var roll = _p.StartRoll + (_p.EndRoll - _p.StartRoll) * h;
        float cs = MathF.Cos(roll), sn = MathF.Sin(roll);
        var xVec = baseX * cs - baseY * sn;
        var yVec = baseY * cs + baseX * sn;

        var sx = _p.StartScale.X + (_p.EndScale.X - _p.StartScale.X) * h;
        var sy = _p.StartScale.Y + (_p.EndScale.Y - _p.StartScale.Y) * h;

        FVector p, n;
        switch (_axis)
        {
            case 0:
                p = pos + xVec * (y * sx) + yVec * (z * sy);
                n = dir * nx + xVec * ny + yVec * nz;
                break;
            case 1:
                p = pos + yVec * (x * sy) + xVec * (z * sx);
                n = yVec * nx + dir * ny + xVec * nz;
                break;
            default:
                p = pos + xVec * (x * sx) + yVec * (y * sy);
                n = xVec * nx + yVec * ny + dir * nz;
                break;
        }
        x = p.X; y = p.Y; z = p.Z;
        nx = n.X; ny = n.Y; nz = n.Z;
    }
}

public sealed class GeoGroup
{
    public int State;
    public readonly List<float> Pos = new();
    public readonly List<sbyte> Nrm = new();
    public readonly List<float> UV = new();
    public readonly List<uint> Idx = new();
    public int VertCount => Pos.Count / 3;
}

public sealed class LevelGeo
{
    public readonly Dictionary<int, GeoGroup> Groups = new();
    public float[] Min = { float.MaxValue, float.MaxValue, float.MaxValue };
    public float[] Max = { float.MinValue, float.MinValue, float.MinValue };
    // bounds of non-water geometry only (water planes are often enormous)
    public float[] SolidMin = { float.MaxValue, float.MaxValue, float.MaxValue };
    public float[] SolidMax = { float.MinValue, float.MinValue, float.MinValue };
    public readonly HashSet<int> WaterStates = new();
    public bool HasSolid => SolidMin[0] <= SolidMax[0];
    public long Tris, Verts;
    public int Instances;

    public const float Scale = 0.01f; // UE cm -> metres

    private GeoGroup Group(int state)
    {
        if (!Groups.TryGetValue(state, out var g)) Groups[state] = g = new GeoGroup { State = state };
        return g;
    }

    private static sbyte Q(float v) => (sbyte) Math.Clamp((int) MathF.Round(v * 127f), -127, 127);

    /// <summary>
    /// Bake one mesh instance into the level. Output space is three.js-style: (x, z, y) in metres, CCW front faces.
    /// <paramref name="spline"/> optionally deforms vertices along a spline before the world transform.
    /// </summary>
    public void Emit(MeshData mesh, Func<int, MatInfo> matForSlot, in Affine world, SplineDeformer spline = null)
    {
        Instances++;
        mesh.Instances++;
        var det = world.Det;
        if (spline != null && spline.Flips) det = -det;
        // local facing * transform handedness * axis swap (a reflection)
        var flip = mesh.Facing * det * -1 < 0;

        foreach (var sec in mesh.Sections)
        {
            var mat = matForSlot(sec.Slot);
            if (mat == null || mat.Skip || mat.State < 0) continue;
            mat.Tris += sec.Tri.Length / 3;
            var g = Group(mat.State);
            if (mat.Water) WaterStates.Add(mat.State);
            var solid = !mat.Water;
            var baseV = (uint) g.VertCount;
            foreach (var vi in sec.VertIdx)
            {
                float x = mesh.Pos[vi * 3], y = mesh.Pos[vi * 3 + 1], z = mesh.Pos[vi * 3 + 2];
                float nx = mesh.Nrm[vi * 3], ny = mesh.Nrm[vi * 3 + 1], nz = mesh.Nrm[vi * 3 + 2];
                spline?.Apply(ref x, ref y, ref z, ref nx, ref ny, ref nz);
                var p = world.Pos(x, y, z);
                var nn = world.Normal(nx, ny, nz);
                float ox = p.X * Scale, oy = p.Z * Scale, oz = p.Y * Scale;
                g.Pos.Add(ox); g.Pos.Add(oy); g.Pos.Add(oz);
                g.Nrm.Add(Q(nn.X)); g.Nrm.Add(Q(nn.Z)); g.Nrm.Add(Q(nn.Y)); g.Nrm.Add(0);
                g.UV.Add(mesh.UV[vi * 2]); g.UV.Add(mesh.UV[vi * 2 + 1]);
                if (ox < Min[0]) Min[0] = ox; if (oy < Min[1]) Min[1] = oy; if (oz < Min[2]) Min[2] = oz;
                if (ox > Max[0]) Max[0] = ox; if (oy > Max[1]) Max[1] = oy; if (oz > Max[2]) Max[2] = oz;
                if (solid)
                {
                    if (ox < SolidMin[0]) SolidMin[0] = ox; if (oy < SolidMin[1]) SolidMin[1] = oy; if (oz < SolidMin[2]) SolidMin[2] = oz;
                    if (ox > SolidMax[0]) SolidMax[0] = ox; if (oy > SolidMax[1]) SolidMax[1] = oy; if (oz > SolidMax[2]) SolidMax[2] = oz;
                }
            }
            var tri = sec.Tri;
            for (var k = 0; k < tri.Length; k += 3)
            {
                g.Idx.Add(baseV + (uint) tri[k]);
                if (flip)
                {
                    g.Idx.Add(baseV + (uint) tri[k + 2]);
                    g.Idx.Add(baseV + (uint) tri[k + 1]);
                }
                else
                {
                    g.Idx.Add(baseV + (uint) tri[k + 1]);
                    g.Idx.Add(baseV + (uint) tri[k + 2]);
                }
            }
            Tris += tri.Length / 3;
            Verts += sec.VertIdx.Length;
        }
    }

    private struct CV { public float X, Y, Z, U, V; public sbyte Nx, Ny, Nz; }

    /// <summary>Clip water surfaces to the footprint of the level's solid geometry (plus a margin), so a kilometre-wide sea plane
    /// does not blanket unrelated zones that happen to sit above or below it.</summary>
    public void ClipWater(float margin)
    {
        if (!HasSolid) return;
        float x0 = SolidMin[0] - margin, x1 = SolidMax[0] + margin, z0 = SolidMin[2] - margin, z1 = SolidMax[2] + margin;
        foreach (var st in WaterStates)
        {
            if (!Groups.TryGetValue(st, out var g) || g.Idx.Count == 0) continue;
            var outPos = new List<float>(); var outNrm = new List<sbyte>(); var outUv = new List<float>(); var outIdx = new List<uint>();
            var poly = new List<CV>(8); var tmp = new List<CV>(8);
            for (var i = 0; i < g.Idx.Count; i += 3)
            {
                poly.Clear();
                for (var k = 0; k < 3; k++)
                {
                    var vi = (int) g.Idx[i + k];
                    poly.Add(new CV { X = g.Pos[vi * 3], Y = g.Pos[vi * 3 + 1], Z = g.Pos[vi * 3 + 2], U = g.UV[vi * 2], V = g.UV[vi * 2 + 1], Nx = g.Nrm[vi * 4], Ny = g.Nrm[vi * 4 + 1], Nz = g.Nrm[vi * 4 + 2] });
                }
                ClipAxis(poly, tmp, 0, x0, true); ClipAxis(tmp, poly, 0, x1, false);
                ClipAxis(poly, tmp, 2, z0, true); ClipAxis(tmp, poly, 2, z1, false);
                if (poly.Count < 3) continue;
                var b = (uint) (outPos.Count / 3);
                foreach (var v in poly)
                {
                    outPos.Add(v.X); outPos.Add(v.Y); outPos.Add(v.Z);
                    outNrm.Add(v.Nx); outNrm.Add(v.Ny); outNrm.Add(v.Nz); outNrm.Add(0);
                    outUv.Add(v.U); outUv.Add(v.V);
                }
                for (var k = 1; k + 1 < poly.Count; k++) { outIdx.Add(b); outIdx.Add(b + (uint) k); outIdx.Add(b + (uint) k + 1); }
            }
            Tris += outIdx.Count / 3 - g.Idx.Count / 3;
            Verts += outPos.Count / 3 - g.VertCount;
            g.Pos.Clear(); g.Pos.AddRange(outPos); g.Nrm.Clear(); g.Nrm.AddRange(outNrm); g.UV.Clear(); g.UV.AddRange(outUv); g.Idx.Clear(); g.Idx.AddRange(outIdx);
        }
    }

    private static void ClipAxis(List<CV> src, List<CV> dst, int axis, float bound, bool keepGreater)
    {
        dst.Clear();
        for (var i = 0; i < src.Count; i++)
        {
            var a = src[i]; var b = src[(i + 1) % src.Count];
            float av = axis == 0 ? a.X : a.Z, bv = axis == 0 ? b.X : b.Z;
            bool ain = keepGreater ? av >= bound : av <= bound, bin = keepGreater ? bv >= bound : bv <= bound;
            if (ain) dst.Add(a);
            if (ain == bin) continue;
            var t = (bound - av) / (bv - av);
            dst.Add(new CV { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t, Z = a.Z + (b.Z - a.Z) * t, U = a.U + (b.U - a.U) * t, V = a.V + (b.V - a.V) * t, Nx = a.Nx, Ny = a.Ny, Nz = a.Nz });
        }
    }

    /// <summary>Binary layout: 'QDG1', groupCount, {state, vertCount, indexCount}*, then per group pos f32*3, nrm i8*4, uv f32*2, idx u32.</summary>
    public void Write(string file)
    {
        using var fs = File.Create(file);
        using var bw = new BinaryWriter(fs);
        var groups = Groups.Values.Where(g => g.Idx.Count > 0).OrderBy(g => g.State).ToArray();
        bw.Write(0x31474451u); // 'QDG1'
        bw.Write((uint) groups.Length);
        foreach (var g in groups)
        {
            bw.Write(g.State);
            bw.Write((uint) g.VertCount);
            bw.Write((uint) g.Idx.Count);
        }
        foreach (var g in groups)
        {
            WriteSpan<float>(bw, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(g.Pos));
            WriteSpan<sbyte>(bw, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(g.Nrm));
            WriteSpan<float>(bw, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(g.UV));
            WriteSpan<uint>(bw, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(g.Idx));
        }
    }

    private static void WriteSpan<T>(BinaryWriter bw, Span<T> span) where T : unmanaged =>
        bw.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
}
