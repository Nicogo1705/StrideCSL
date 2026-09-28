using System;
using Csl.Types;

namespace Csl.Cpu;

/// <summary>
/// What the members of the resource types (Texture2D, RWTexture2D, StructuredBuffer…) do when the
/// resource holds CPU data: a <see cref="CpuTexture"/>, or an array for a buffer. Without, they throw
/// as they always did: the resource lives on the GPU. <c>dimensions</c> is 1 to 3,
/// <c>array</c> tells whether the coordinate carries an element index after them.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public static class ResourceOps
{
    private static CpuTexture Texture(object? cpu) => cpu as CpuTexture
        ?? throw new NotSupportedException("This texture has no CPU data: it is only accessed on the GPU (give the shader a CpuTexture to run it on the CPU)");

    private static T[] Buffer<T>(object? cpu) => cpu as T[]
        ?? throw new NotSupportedException("This buffer has no CPU data: it is only accessed on the GPU (give the shader an array to run it on the CPU)");

    // -- coordinates ---------------------------------------------------------------------------------

    public static float4 F(float v) => new float4(v, 0f, 0f, 0f);
    public static float4 F(float2 v) => new float4(v.x, v.y, 0f, 0f);
    public static float4 F(float3 v) => new float4(v.x, v.y, v.z, 0f);
    public static float4 F(float4 v) => v;
    public static int4 I(int v) => new int4(v, 0, 0, 0);
    public static int4 I(int2 v) => new int4(v.x, v.y, 0, 0);
    public static int4 I(int3 v) => new int4(v.x, v.y, v.z, 0);
    public static int4 I(int4 v) => v;
    public static int4 I(uint v) => new int4((int)v, 0, 0, 0);
    public static int4 I(uint2 v) => new int4((int)v.x, (int)v.y, 0, 0);
    public static int4 I(uint3 v) => new int4((int)v.x, (int)v.y, (int)v.z, 0);
    public static int4 I(uint4 v) => new int4((int)v.x, (int)v.y, (int)v.z, (int)v.w);

    private static float At(float4 v, int i) => i switch { 0 => v.x, 1 => v.y, 2 => v.z, _ => v.w };
    private static int At(int4 v, int i) => i switch { 0 => v.x, 1 => v.y, 2 => v.z, _ => v.w };

    /// <summary>An array index from a float coordinate: rounded to the nearest even, clamped (D3D).</summary>
    private static int Layer(CpuTexture texture, float value) => Math.Clamp((int)MathF.Round(value, MidpointRounding.ToEven), 0, texture.Depth - 1);

    // -- texel values --------------------------------------------------------------------------------

    public static T As<T>(float4 v)
    {
        if (typeof(T) == typeof(float4)) return (T)(object)v;
        if (typeof(T) == typeof(float)) return (T)(object)v.x;
        if (typeof(T) == typeof(float2)) return (T)(object)new float2(v.x, v.y);
        if (typeof(T) == typeof(float3)) return (T)(object)new float3(v.x, v.y, v.z);
        return HlslConvert.Convert<T>(v);
    }

    public static T AsInteger<T>(uint4 v)
    {
        if (typeof(T) == typeof(uint)) return (T)(object)v.x;
        if (typeof(T) == typeof(uint4)) return (T)(object)v;
        if (typeof(T) == typeof(int)) return (T)(object)(int)v.x;
        return HlslConvert.Convert<T>(new int4((int)v.x, (int)v.y, (int)v.z, (int)v.w));
    }

    public static float4 ToFloat4<T>(T value)
    {
        if (value is float4 f4) return f4;
        if (value is float f) return new float4(f, 0f, 0f, 1f);
        if (value is float2 f2) return new float4(f2.x, f2.y, 0f, 1f);
        if (value is float3 f3) return new float4(f3.x, f3.y, f3.z, 1f);
        return HlslConvert.Convert<float4>(value!);
    }

    private static uint4 ToUInt4<T>(T value)
    {
        if (value is uint u) return new uint4(u, 0, 0, 0);
        if (value is int i) return new uint4((uint)i, 0, 0, 0);
        if (value is uint4 u4) return u4;
        return HlslConvert.Convert<uint4>(value!);
    }

    private static T Read<T>(CpuTexture texture, int level, int x, int y, int z)
    {
        if (level < 0 || level >= texture.MipLevels || x < 0 || y < 0 || z < 0
            || x >= texture.LevelWidth(level) || y >= texture.LevelHeight(level) || z >= texture.LevelDepth(level))
            return texture.IsInteger ? AsInteger<T>(default) : As<T>(default);
        return texture.IsInteger ? AsInteger<T>(texture.ReadInteger(level, x, y, z)) : As<T>(texture.Read(level, x, y, z));
    }

    // -- reads ---------------------------------------------------------------------------------------

    /// <summary>tex[location], or Load(location) with <paramref name="withLevel"/>: the level after the coordinates.</summary>
    public static T Load<T>(object? cpu, int dimensions, bool array, int4 location, bool withLevel, int4 offset = default)
    {
        var texture = Texture(cpu);
        int count = dimensions + (array ? 1 : 0);
        int level = withLevel ? At(location, count) : 0;
        int x = location.x + offset.x;
        int y = dimensions > 1 ? location.y + offset.y : 0;
        int z = dimensions > 2 ? location.z + offset.z : array ? At(location, dimensions) : 0;
        return Read<T>(texture, level, x, y, z);
    }

    public enum Level { Implicit, Explicit, Bias, Gradient, Zero }

    public static T Sample<T>(object? cpu, SamplerState sampler, int dimensions, bool array, float4 location, Level mode, float value = 0f, int4 offset = default, float4 ddx = default, float4 ddy = default)
    {
        var texture = Texture(cpu);
        int layer = array ? Layer(texture, At(location, dimensions)) : 0;
        float lod = LevelOf(texture, sampler.Description, dimensions, location, mode, value, ddx, ddy);
        return As<T>(Sampling.Sample(texture, sampler.Description, dimensions, location, layer, lod, offset));
    }

    public static float SampleCmp(object? cpu, SamplerComparisonState sampler, int dimensions, bool array, float4 location, float compare, bool levelZero, int4 offset = default)
    {
        var texture = Texture(cpu);
        int layer = array ? Layer(texture, At(location, dimensions)) : 0;
        float lod = levelZero ? 0f : LevelOf(texture, sampler.Description, dimensions, location, Level.Implicit, 0f, default, default);
        return Sampling.Sample(texture, sampler.Description, dimensions, location, layer, lod, offset, compare).x;
    }

    public static float4 Gather(object? cpu, SamplerState sampler, bool array, float4 location, int channel, int4 offset = default)
    {
        var texture = Texture(cpu);
        int layer = array ? Layer(texture, location.z) : 0;
        return Sampling.Gather(texture, sampler.Description, location, layer, offset, channel);
    }

    public static float4 GatherCmp(object? cpu, SamplerComparisonState sampler, bool array, float4 location, float compare)
    {
        var texture = Texture(cpu);
        int layer = array ? Layer(texture, location.z) : 0;
        return Sampling.Gather(texture, sampler.Description, location, layer, default, 0, compare);
    }

    // -- cube maps: the face the direction points at (D3D's rules), sampled as a 2D slice ----------------
    // Filtering stops at the face's edge (clamped) where the GPU blends in the next face: a texel's
    // difference along the seams.

    /// <summary>The face (a slice: +X, -X, +Y, -Y, +Z, -Z) and its (s, t, |major axis|) for a direction.</summary>
    private static (int Face, float S, float T, float Major) CubeFace(float3 d)
    {
        float ax = MathF.Abs(d.x), ay = MathF.Abs(d.y), az = MathF.Abs(d.z);
        if (az >= ax && az >= ay)
            return d.z >= 0 ? (4, d.x, -d.y, az) : (5, -d.x, -d.y, az);
        if (ay >= ax)
            return d.y >= 0 ? (2, d.x, d.z, ay) : (3, d.x, -d.z, ay);
        return d.x >= 0 ? (0, -d.z, -d.y, ax) : (1, d.z, -d.y, ax);
    }

    /// <summary>The (s, t, major) of another direction on the given face: the derivatives project on the centre's face.</summary>
    private static float3 OnFace(int face, float3 d) => face switch
    {
        0 => new float3(-d.z, -d.y, d.x),
        1 => new float3(d.z, -d.y, -d.x),
        2 => new float3(d.x, d.z, d.y),
        3 => new float3(d.x, -d.z, -d.y),
        4 => new float3(d.x, -d.y, d.z),
        _ => new float3(-d.x, -d.y, -d.z),
    };

    private static SamplerDescription Clamped(SamplerDescription s) => new()
    {
        MinFilter = s.MinFilter, MagFilter = s.MagFilter, MipFilter = s.MipFilter, Anisotropic = s.Anisotropic,
        MipLodBias = s.MipLodBias, MinLod = s.MinLod, MaxLod = s.MaxLod, Compare = s.Compare, BorderColor = s.BorderColor,
    };

    /// <summary>The face's texture coordinates and slice for a direction (and a cube index of an array).</summary>
    private static (float4 Uv, int Layer, int Face, float3 Sc) CubeCoordinate(CpuTexture texture, bool array, float4 location)
    {
        var direction = new float3(location.x, location.y, location.z);
        var (face, s, t, major) = CubeFace(direction);
        var uv = new float4(0.5f * (s / major) + 0.5f, 0.5f * (t / major) + 0.5f, 0f, 0f);
        int layer = face + (array ? 6 * (int)MathF.Round(location.w) : 0);
        return (uv, Math.Clamp(layer, 0, texture.Depth - 1), face, new float3(s, t, major));
    }

    /// <summary>A direction's derivative as the face coordinates' derivative: d(0.5 s / ma) with the quotient rule.</summary>
    private static float4 FaceDerivative(int face, float3 sc, float4 derivative)
    {
        var d = OnFace(face, new float3(derivative.x, derivative.y, derivative.z));
        float ma = sc.z;
        return new float4(0.5f * (d.x * ma - sc.x * d.z) / (ma * ma), 0.5f * (d.y * ma - sc.y * d.z) / (ma * ma), 0f, 0f);
    }

    private static float CubeLevel(CpuTexture texture, SamplerDescription? sampler, float4 location, int face, float3 sc, Level mode, float value, float4 ddx, float4 ddy)
    {
        switch (mode)
        {
            case Level.Explicit:
                return value;
            case Level.Zero:
                return 0f;
            case Level.Gradient:
                return Sampling.LevelOfDetail(texture, 2, FaceDerivative(face, sc, ddx), FaceDerivative(face, sc, ddy));
        }
        float lod = 0f;
        bool matters = texture.MipLevels > 1 || sampler == null || sampler.MinFilter != sampler.MagFilter;
        if (matters && Lane.Current is { IsPixel: true })
        {
            var (dx, dy) = Lane.Derivatives(location);
            lod = Sampling.LevelOfDetail(texture, 2, FaceDerivative(face, sc, dx), FaceDerivative(face, sc, dy));
        }
        else if (texture.MipLevels > 1)
        {
            throw new InvalidOperationException("Sample picks its level from the 2x2 quad: only in a pixel shader run (use SampleLevel elsewhere).");
        }
        return mode == Level.Bias ? lod + value : lod;
    }

    public static T SampleCube<T>(object? cpu, SamplerState sampler, bool array, float4 location, Level mode, float value = 0f, float4 ddx = default, float4 ddy = default)
    {
        var texture = Texture(cpu);
        var (uv, layer, face, sc) = CubeCoordinate(texture, array, location);
        float lod = CubeLevel(texture, sampler.Description, location, face, sc, mode, value, ddx, ddy);
        return As<T>(Sampling.Sample(texture, Clamped(sampler.Description), 2, uv, layer, lod, default));
    }

    public static float SampleCmpCube(object? cpu, SamplerComparisonState sampler, bool array, float4 location, float compare, bool levelZero)
    {
        var texture = Texture(cpu);
        var (uv, layer, face, sc) = CubeCoordinate(texture, array, location);
        float lod = levelZero ? 0f : CubeLevel(texture, sampler.Description, location, face, sc, Level.Implicit, 0f, default, default);
        return Sampling.Sample(texture, Clamped(sampler.Description), 2, uv, layer, lod, default, compare).x;
    }

    public static float4 GatherCube(object? cpu, SamplerState sampler, bool array, float4 location, int channel)
    {
        var texture = Texture(cpu);
        var (uv, layer, _, _) = CubeCoordinate(texture, array, location);
        return Sampling.Gather(texture, Clamped(sampler.Description), uv, layer, default, channel);
    }

    public static float4 GatherCmpCube(object? cpu, SamplerComparisonState sampler, bool array, float4 location, float compare)
    {
        var texture = Texture(cpu);
        var (uv, layer, _, _) = CubeCoordinate(texture, array, location);
        return Sampling.Gather(texture, Clamped(sampler.Description), uv, layer, default, 0, compare);
    }

    public static float CalculateLevelOfDetailCube(object? cpu, SamplerState sampler, float4 location)
    {
        var texture = Texture(cpu);
        var (_, _, face, sc) = CubeCoordinate(texture, false, location);
        var lod = CubeLevel(texture, null, location, face, sc, Level.Implicit, 0f, default, default) + sampler.Description.MipLodBias;
        return Math.Clamp(lod, Math.Max(0f, sampler.Description.MinLod), Math.Min(texture.MipLevels - 1, sampler.Description.MaxLod));
    }

    public static float CalculateLevelOfDetail(object? cpu, SamplerState sampler, int dimensions, float4 location)
    {
        var texture = Texture(cpu);
        var lod = LevelOf(texture, null, dimensions, location, Level.Implicit, 0f, default, default) + sampler.Description.MipLodBias;
        return Math.Clamp(lod, Math.Max(0f, sampler.Description.MinLod), Math.Min(texture.MipLevels - 1, sampler.Description.MaxLod));
    }

    private static float LevelOf(CpuTexture texture, SamplerDescription? sampler, int dimensions, float4 location, Level mode, float value, float4 ddx, float4 ddy)
    {
        switch (mode)
        {
            case Level.Explicit:
                return value;
            case Level.Zero:
                return 0f;
            case Level.Gradient:
                return Sampling.LevelOfDetail(texture, dimensions, ddx, ddy);
        }
        // Implicit: from the quad, as the GPU does. With one level and the same filter for minifying and
        // magnifying, the level changes nothing: no need to wait for the neighbours.
        float lod = 0f;
        bool matters = texture.MipLevels > 1 || sampler == null || sampler.MinFilter != sampler.MagFilter;
        if (matters && Lane.Current is { IsPixel: true })
        {
            var (dx, dy) = Lane.Derivatives(location);
            lod = Sampling.LevelOfDetail(texture, dimensions, dx, dy);
        }
        else if (texture.MipLevels > 1)
        {
            throw new InvalidOperationException("Sample picks its level from the 2x2 quad: only in a pixel shader run (use SampleLevel elsewhere).");
        }
        return mode == Level.Bias ? lod + value : lod;
    }

    public static void Dimensions(object? cpu, int dimensions, bool array, uint mipLevel, out uint width, out uint height, out uint depth, out uint levels)
    {
        var texture = Texture(cpu);
        int level = (int)mipLevel;
        width = (uint)texture.LevelWidth(level);
        height = (uint)texture.LevelHeight(level);
        depth = (uint)(array ? texture.Depth : texture.LevelDepth(level));
        levels = (uint)texture.MipLevels;
    }

    // -- writes --------------------------------------------------------------------------------------

    public static void Store<T>(object? cpu, int dimensions, bool array, int4 location, T value)
    {
        var texture = Texture(cpu);
        int x = location.x;
        int y = dimensions > 1 ? location.y : 0;
        int z = dimensions > 2 ? location.z : array ? At(location, dimensions) : 0;
        // Out of bounds, a write does nothing (D3D).
        if (x < 0 || y < 0 || z < 0 || x >= texture.Width || y >= texture.Height || z >= texture.Depth)
            return;
        // No lock: threads write their own texels; two writing one texel race on the GPU too.
        if (texture.IsInteger)
            texture.WriteInteger(0, x, y, z, ToUInt4(value));
        else
            texture.Write(0, x, y, z, ToFloat4(value));
    }

    // -- buffers -------------------------------------------------------------------------------------

    public static T BufferLoad<T>(object? cpu, long index)
    {
        var data = Buffer<T>(cpu);
        // Out of bounds, a read gives 0 (D3D).
        return index >= 0 && index < data.Length ? data[index] : default!;
    }

    public static void BufferStore<T>(object? cpu, long index, T value)
    {
        var data = Buffer<T>(cpu);
        if (index >= 0 && index < data.Length)
            data[index] = value;
    }

    public static uint BufferCount<T>(object? cpu) => (uint)Buffer<T>(cpu).Length;

    public static uint BufferStride<T>() => (uint)System.Runtime.InteropServices.Marshal.SizeOf<T>();

    public static uint LoadWord(object? cpu, long address) => BufferLoad<uint>(cpu, address / 4);

    public static void StoreWord(object? cpu, long address, uint value) => BufferStore(cpu, address / 4, value);
}
