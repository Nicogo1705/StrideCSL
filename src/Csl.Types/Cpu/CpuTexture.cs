using System;
using Csl.Types;

namespace Csl.Cpu;

/// <summary>How a texel is stored on the GPU: what a write rounds to, and how a read decodes it.</summary>
public enum TexelFormat
{
    Rgba32Float,
    Rgba16Float,
    Rgba8UNorm,
    Rgba8UNormSrgb,
    Rg32Float,
    R32Float,
    R16Float,
    R8UNorm,
    Rgba32UInt,
    R32UInt,
    R32SInt,
}

/// <summary>
/// A texture for shader code run on the CPU: its texels decoded to float4 (or uint4 for the integer
/// formats) per mip level, one slice after the other for a 3D texture or an array. What is written
/// through a RW view is rounded to the format as the GPU stores it: a UNorm8 texture holds n / 255.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class CpuTexture
{
    private readonly float4[][] texels;
    private readonly uint4[][] integers;

    /// <remarks><c>depth</c>: slices of a 3D texture, or elements of an array; 1 otherwise.</remarks>
    public CpuTexture(int width, int height = 1, int depth = 1, int mipLevels = 1, TexelFormat format = TexelFormat.Rgba32Float)
    {
        if (width < 1 || height < 1 || depth < 1 || mipLevels < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "a texture has at least one texel and one level");
        Width = width;
        Height = height;
        Depth = depth;
        MipLevels = mipLevels;
        Format = format;
        texels = new float4[mipLevels][];
        integers = new uint4[mipLevels][];
        for (int level = 0; level < mipLevels; level++)
        {
            int count = LevelWidth(level) * LevelHeight(level) * depth;
            if (IsInteger)
                integers[level] = new uint4[count];
            else
                texels[level] = new float4[count];
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public int MipLevels { get; }
    public TexelFormat Format { get; }

    /// <summary>A 3D texture: its depth shrinks with the level, where an array keeps its elements.</summary>
    public bool IsVolume { get; init; }

    public bool IsInteger => Format is TexelFormat.Rgba32UInt or TexelFormat.R32UInt or TexelFormat.R32SInt;

    public int LevelWidth(int level) => Math.Max(1, Width >> level);
    public int LevelHeight(int level) => Math.Max(1, Height >> level);
    public int LevelDepth(int level) => IsVolume ? Math.Max(1, Depth >> level) : Depth;

    private int Index(int level, int x, int y, int z) => (z * LevelHeight(level) + y) * LevelWidth(level) + x;

    /// <summary>The texel as a float shader reads it: decoded, sRGB made linear.</summary>
    public float4 Read(int level, int x, int y, int z = 0)
    {
        if (IsInteger)
        {
            var i = integers[level][Index(level, x, y, z)];
            return new float4(i.x, i.y, i.z, i.w);
        }
        return texels[level][Index(level, x, y, z)];
    }

    /// <summary>The texel of an integer format, as its bits.</summary>
    public uint4 ReadInteger(int level, int x, int y, int z = 0)
    {
        if (IsInteger)
            return integers[level][Index(level, x, y, z)];
        var f = texels[level][Index(level, x, y, z)];
        return new uint4((uint)f.x, (uint)f.y, (uint)f.z, (uint)f.w);
    }

    /// <summary>Stores a value as the GPU would: rounded to the format, the missing channels dropped.</summary>
    public void Write(int level, int x, int y, int z, float4 value)
    {
        if (IsInteger)
        {
            integers[level][Index(level, x, y, z)] = Truncate(new uint4((uint)value.x, (uint)value.y, (uint)value.z, (uint)value.w));
            return;
        }
        texels[level][Index(level, x, y, z)] = Encode(value);
    }

    public void WriteInteger(int level, int x, int y, int z, uint4 value)
    {
        if (IsInteger)
            integers[level][Index(level, x, y, z)] = Truncate(value);
        else
            texels[level][Index(level, x, y, z)] = Encode(new float4(value.x, value.y, value.z, value.w));
    }

    /// <summary>Fills level 0 from values as they are stored, a slice after the other (row by row).</summary>
    public CpuTexture Fill(ReadOnlySpan<float4> values)
    {
        int width = LevelWidth(0), height = LevelHeight(0);
        for (int i = 0; i < values.Length && i < width * height * Depth; i++)
            Write(0, i % width, i / width % height, i / (width * height), values[i]);
        return this;
    }

    /// <summary>Fills level 0 from 8-bit RGBA, as a PNG or a Stride Color array holds it (sRGB decoded for an sRGB format).</summary>
    public CpuTexture FillRgba8(ReadOnlySpan<byte> rgba)
    {
        int width = LevelWidth(0), height = LevelHeight(0);
        bool srgb = Format == TexelFormat.Rgba8UNormSrgb;
        for (int i = 0; i * 4 + 3 < rgba.Length && i < width * height * Depth; i++)
        {
            var v = new float4(rgba[i * 4] / 255f, rgba[i * 4 + 1] / 255f, rgba[i * 4 + 2] / 255f, rgba[i * 4 + 3] / 255f);
            if (srgb)
                v = new float4(SrgbToLinear(v.x), SrgbToLinear(v.y), SrgbToLinear(v.z), v.w);
            texels[0][Index(0, i % width, i / width % height, i / (width * height))] = v;
        }
        return this;
    }

    /// <summary>Level 0, as stored, row by row: what a readback of the GPU texture gives.</summary>
    public float4[] ToArray(int level = 0)
    {
        var result = new float4[LevelWidth(level) * LevelHeight(level) * LevelDepth(level)];
        for (int i = 0; i < result.Length; i++)
            result[i] = IsInteger ? Read(level, i % LevelWidth(level), i / LevelWidth(level) % LevelHeight(level), i / (LevelWidth(level) * LevelHeight(level))) : texels[level][i];
        return result;
    }

    private uint4 Truncate(uint4 v) => Format switch
    {
        TexelFormat.R32UInt or TexelFormat.R32SInt => new uint4(v.x, 0, 0, 1),
        _ => v,
    };

    private float4 Encode(float4 v) => Format switch
    {
        TexelFormat.Rgba32Float => v,
        TexelFormat.Rgba16Float => new float4(Half(v.x), Half(v.y), Half(v.z), Half(v.w)),
        TexelFormat.Rgba8UNorm => new float4(UNorm8(v.x), UNorm8(v.y), UNorm8(v.z), UNorm8(v.w)),
        TexelFormat.Rgba8UNormSrgb => new float4(SrgbToLinear(UNorm8(LinearToSrgb(v.x))), SrgbToLinear(UNorm8(LinearToSrgb(v.y))), SrgbToLinear(UNorm8(LinearToSrgb(v.z))), UNorm8(v.w)),
        TexelFormat.Rg32Float => new float4(v.x, v.y, 0f, 1f),
        TexelFormat.R32Float => new float4(v.x, 0f, 0f, 1f),
        TexelFormat.R16Float => new float4(Half(v.x), 0f, 0f, 1f),
        TexelFormat.R8UNorm => new float4(UNorm8(v.x), 0f, 0f, 1f),
        _ => v,
    };

    private static float Half(float v) => (float)(System.Half)v;

    /// <summary>D3D's float to UNORM: clamped, NaN to 0, rounded to the nearest of the 256 values.</summary>
    private static float UNorm8(float v) => float.IsNaN(v) ? 0f : MathF.Round(Math.Clamp(v, 0f, 1f) * 255f) / 255f;

    public static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    public static float LinearToSrgb(float v) => float.IsNaN(v) ? 0f : v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
}
