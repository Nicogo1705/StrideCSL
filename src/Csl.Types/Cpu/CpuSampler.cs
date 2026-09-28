using System;
using System.Text.RegularExpressions;
using Csl.Types;

namespace Csl.Cpu;

public enum FilterMode { Point, Linear }

public enum AddressMode { Wrap, Mirror, Clamp, Border, MirrorOnce }

public enum CompareFunction { Never, Less, Equal, LessEqual, Greater, NotEqual, GreaterEqual, Always }

/// <summary>
/// A sampler state for shader code run on the CPU, with Stride's defaults (linear, clamp, border
/// (0, 0, 0, 0)): what a SamplerState no one described gets, as the engine's Texturing.Sampler.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class SamplerDescription
{
    public FilterMode MinFilter = FilterMode.Linear;
    public FilterMode MagFilter = FilterMode.Linear;
    public FilterMode MipFilter = FilterMode.Linear;
    /// <summary>Sampled as linear: the footprint along the longest derivative is not walked.</summary>
    public bool Anisotropic;
    public AddressMode AddressU = AddressMode.Clamp;
    public AddressMode AddressV = AddressMode.Clamp;
    public AddressMode AddressW = AddressMode.Clamp;
    public float4 BorderColor;
    public float MipLodBias;
    public float MinLod = -float.MaxValue;
    public float MaxLod = float.MaxValue;
    public CompareFunction Compare = CompareFunction.Never;

    public static readonly SamplerDescription Default = new SamplerDescription();

    private static readonly Regex Token = new Regex("MIN|MAG|MIP|POINT|LINEAR|ANISOTROPIC", RegexOptions.Compiled);

    /// <summary>
    /// A filter as SDSL writes it, in D3D's words (MIN_MAG_POINT_MIP_LINEAR, COMPARISON_…) or Stride's
    /// (Point, MinPointMagMipLinear, ComparisonLinear…).
    /// </summary>
    public static void ParseFilter(string text, SamplerDescription into)
    {
        var upper = text.Replace("_", string.Empty).ToUpperInvariant();
        if (upper.Contains("ANISOTROPIC"))
        {
            into.MinFilter = into.MagFilter = into.MipFilter = FilterMode.Linear;
            into.Anisotropic = true;
            return;
        }
        var pending = new System.Collections.Generic.List<string>();
        bool any = false;
        foreach (Match match in Token.Matches(upper))
        {
            if (match.Value is "MIN" or "MAG" or "MIP")
            {
                pending.Add(match.Value);
                continue;
            }
            var mode = match.Value == "POINT" ? FilterMode.Point : FilterMode.Linear;
            // "Point" alone, "ComparisonLinear": every stage.
            if (pending.Count == 0 && !any)
                pending.AddRange(new[] { "MIN", "MAG", "MIP" });
            foreach (var stage in pending)
            {
                if (stage == "MIN") into.MinFilter = mode;
                else if (stage == "MAG") into.MagFilter = mode;
                else into.MipFilter = mode;
            }
            pending.Clear();
            any = true;
        }
    }

    public static AddressMode ParseAddress(string text) => text.Replace("_", string.Empty).ToUpperInvariant() switch
    {
        "WRAP" => AddressMode.Wrap,
        "MIRROR" => AddressMode.Mirror,
        "CLAMP" => AddressMode.Clamp,
        "BORDER" => AddressMode.Border,
        "MIRRORONCE" => AddressMode.MirrorOnce,
        _ => throw new FormatException("Unknown address mode " + text),
    };

    public static CompareFunction ParseCompare(string text) => (CompareFunction)Enum.Parse(typeof(CompareFunction), text.Replace("_", string.Empty), ignoreCase: true);

    /// <summary>The description an attribute gives: <c>[Sampler(Filter = "MIN_MAG_MIP_POINT", AddressU = "Wrap")]</c>.</summary>
    public static SamplerDescription From(SamplerAttribute attribute)
    {
        var description = new SamplerDescription();
        if (attribute.Filter != null) ParseFilter(attribute.Filter, description);
        if (attribute.AddressU != null) description.AddressU = ParseAddress(attribute.AddressU);
        if (attribute.AddressV != null) description.AddressV = ParseAddress(attribute.AddressV);
        if (attribute.AddressW != null) description.AddressW = ParseAddress(attribute.AddressW);
        if (attribute.ComparisonFunc != null) description.Compare = ParseCompare(attribute.ComparisonFunc);
        if (attribute.MipLODBias != null) description.MipLodBias = float.Parse(attribute.MipLODBias, System.Globalization.CultureInfo.InvariantCulture);
        if (attribute.MinLOD != null) description.MinLod = float.Parse(attribute.MinLOD, System.Globalization.CultureInfo.InvariantCulture);
        if (attribute.MaxLOD != null) description.MaxLod = float.Parse(attribute.MaxLOD, System.Globalization.CultureInfo.InvariantCulture);
        if (attribute.BorderColor != null) description.BorderColor = ParseColor(attribute.BorderColor);
        return description;
    }

    private static float4 ParseColor(string text)
    {
        var numbers = Regex.Matches(text, @"-?\d+(\.\d+)?");
        float At(int i) => i < numbers.Count ? float.Parse(numbers[i].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f;
        return new float4(At(0), At(1), At(2), At(3));
    }
}

/// <summary>
/// Texture filtering as Direct3D 11 specifies it: the address mode applied to texel indices, linear
/// taps at u · size - 0.5 weighted with <see cref="SubtexelBits"/> bits of fraction, the level from the
/// longest derivative, mip levels blended with the same precision.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public static class Sampling
{
    /// <summary>Bits of the linear weights: 8 on the hardware D3D11 describes (and on the GPUs measured).</summary>
    public static int SubtexelBits = 8;

    private static float Quantize(float fraction)
    {
        float steps = 1 << SubtexelBits;
        return MathF.Round(fraction * steps) / steps;
    }

    /// <summary>
    /// The level of detail for coordinates changing by <paramref name="ddx"/> and <paramref name="ddy"/>
    /// per pixel (normalized, <paramref name="dimensions"/> of them), before bias and clamps.
    /// </summary>
    public static float LevelOfDetail(CpuTexture texture, int dimensions, float4 ddx, float4 ddy)
    {
        float sx = ddx.x * texture.Width, sy = ddy.x * texture.Width;
        float lx = sx * sx, ly = sy * sy;
        if (dimensions > 1)
        {
            float tx = ddx.y * texture.Height, ty = ddy.y * texture.Height;
            lx += tx * tx;
            ly += ty * ty;
        }
        if (dimensions > 2)
        {
            float tx = ddx.z * texture.Depth, ty = ddy.z * texture.Depth;
            lx += tx * tx;
            ly += ty * ty;
        }
        return 0.5f * MathF.Log2(MathF.Max(lx, ly));
    }

    /// <summary>
    /// A filtered sample. <paramref name="coordinate"/> holds <paramref name="dimensions"/> normalized
    /// coordinates; <paramref name="layer"/> is the array element; <paramref name="lod"/> the level
    /// before bias and clamps.
    /// </summary>
    public static float4 Sample(CpuTexture texture, SamplerDescription sampler, int dimensions, float4 coordinate, int layer, float lod, int4 offset, float? compare = null)
    {
        lod += sampler.MipLodBias;
        lod = Math.Clamp(lod, sampler.MinLod, sampler.MaxLod);
        bool magnify = lod <= 0f;
        var filter = magnify ? sampler.MagFilter : sampler.MinFilter;
        float level = Math.Clamp(lod, 0f, texture.MipLevels - 1);
        if (sampler.MipFilter == FilterMode.Point || texture.MipLevels == 1 || magnify)
        {
            int nearest = sampler.MipFilter == FilterMode.Point ? (int)MathF.Floor(level + 0.5f) : (int)MathF.Floor(level);
            if (magnify) nearest = 0;
            return Filter(texture, sampler, filter, dimensions, coordinate, layer, Math.Min(nearest, texture.MipLevels - 1), offset, compare);
        }
        int low = (int)MathF.Floor(level);
        int high = Math.Min(low + 1, texture.MipLevels - 1);
        float t = Quantize(level - low);
        var a = Filter(texture, sampler, filter, dimensions, coordinate, layer, low, offset, compare);
        if (t == 0f || high == low)
            return a;
        var b = Filter(texture, sampler, filter, dimensions, coordinate, layer, high, offset, compare);
        return a + (b - a) * t;
    }

    /// <summary>The four texels a bilinear sample reads, their red channel (or <paramref name="channel"/>): D3D's Gather order.</summary>
    public static float4 Gather(CpuTexture texture, SamplerDescription sampler, float4 coordinate, int layer, int4 offset, int channel, float? compare = null)
    {
        int width = texture.LevelWidth(0), height = texture.LevelHeight(0);
        float a = coordinate.x * width - 0.5f, b = coordinate.y * height - 0.5f;
        int x0 = (int)MathF.Floor(a) + offset.x, y0 = (int)MathF.Floor(b) + offset.y;
        float Tap(int x, int y)
        {
            var texel = Fetch(texture, sampler, 0, x, y, layer, 2);
            float v = channel switch { 0 => texel.x, 1 => texel.y, 2 => texel.z, _ => texel.w };
            return compare is { } reference ? (Passes(sampler.Compare, reference, v) ? 1f : 0f) : v;
        }
        return new float4(Tap(x0, y0 + 1), Tap(x0 + 1, y0 + 1), Tap(x0 + 1, y0), Tap(x0, y0));
    }

    private static float4 Filter(CpuTexture texture, SamplerDescription sampler, FilterMode filter, int dimensions, float4 coordinate, int layer, int level, int4 offset, float? compare)
    {
        int width = texture.LevelWidth(level);
        int height = dimensions > 1 ? texture.LevelHeight(level) : 1;
        int depth = dimensions > 2 ? texture.LevelDepth(level) : 1;
        if (filter == FilterMode.Point)
        {
            int x = (int)MathF.Floor(coordinate.x * width) + offset.x;
            int y = dimensions > 1 ? (int)MathF.Floor(coordinate.y * height) + offset.y : 0;
            int z = dimensions > 2 ? (int)MathF.Floor(coordinate.z * depth) + offset.z : layer;
            return Compared(Fetch(texture, sampler, level, x, y, z, dimensions), sampler, compare);
        }
        float a = coordinate.x * width - 0.5f;
        int x0 = (int)MathF.Floor(a);
        float fx = Quantize(a - x0);
        x0 += offset.x;
        if (dimensions == 1)
        {
            var left = Compared(Fetch(texture, sampler, level, x0, 0, layer, 1), sampler, compare);
            var right = Compared(Fetch(texture, sampler, level, x0 + 1, 0, layer, 1), sampler, compare);
            return left + (right - left) * fx;
        }
        float b = coordinate.y * height - 0.5f;
        int y0 = (int)MathF.Floor(b);
        float fy = Quantize(b - y0);
        y0 += offset.y;
        float4 Bilinear(int z)
        {
            var t00 = Compared(Fetch(texture, sampler, level, x0, y0, z, dimensions), sampler, compare);
            var t10 = Compared(Fetch(texture, sampler, level, x0 + 1, y0, z, dimensions), sampler, compare);
            var t01 = Compared(Fetch(texture, sampler, level, x0, y0 + 1, z, dimensions), sampler, compare);
            var t11 = Compared(Fetch(texture, sampler, level, x0 + 1, y0 + 1, z, dimensions), sampler, compare);
            var top = t00 + (t10 - t00) * fx;
            var bottom = t01 + (t11 - t01) * fx;
            return top + (bottom - top) * fy;
        }
        if (dimensions == 2)
            return Bilinear(layer);
        float c = coordinate.z * depth - 0.5f;
        int z0 = (int)MathF.Floor(c);
        float fz = Quantize(c - z0);
        z0 += offset.z;
        var front = Bilinear(z0);
        var back = Bilinear(z0 + 1);
        return front + (back - front) * fz;
    }

    private static float4 Compared(float4 texel, SamplerDescription sampler, float? compare)
    {
        if (compare is not { } reference)
            return texel;
        float passed = Passes(sampler.Compare, reference, texel.x) ? 1f : 0f;
        return new float4(passed, passed, passed, passed);
    }

    private static bool Passes(CompareFunction function, float reference, float value) => function switch
    {
        CompareFunction.Never => false,
        CompareFunction.Less => reference < value,
        CompareFunction.Equal => reference == value,
        CompareFunction.LessEqual => reference <= value,
        CompareFunction.Greater => reference > value,
        CompareFunction.NotEqual => reference != value,
        CompareFunction.GreaterEqual => reference >= value,
        _ => true,
    };

    /// <summary>A texel through the address modes; the border colour outside a Border axis.</summary>
    private static float4 Fetch(CpuTexture texture, SamplerDescription sampler, int level, int x, int y, int z, int dimensions)
    {
        x = Address(sampler.AddressU, x, texture.LevelWidth(level));
        if (dimensions > 1)
            y = Address(sampler.AddressV, y, texture.LevelHeight(level));
        if (dimensions > 2)
            z = Address(sampler.AddressW, z, texture.LevelDepth(level));
        else
            z = Math.Clamp(z, 0, texture.LevelDepth(level) - 1);
        if (x < 0 || y < 0 || z < 0)
            return sampler.BorderColor;
        return texture.Read(level, x, y, z);
    }

    private static int Address(AddressMode mode, int i, int n)
    {
        switch (mode)
        {
            case AddressMode.Wrap:
                return ((i % n) + n) % n;
            case AddressMode.Mirror:
            {
                int m = ((i % (2 * n)) + 2 * n) % (2 * n);
                return m < n ? m : 2 * n - 1 - m;
            }
            case AddressMode.MirrorOnce:
                return Math.Clamp(i < 0 ? -1 - i : i, 0, n - 1);
            case AddressMode.Border:
                return i < 0 || i >= n ? -1 : i;
            default:
                return Math.Clamp(i, 0, n - 1);
        }
    }

    /// <summary>A texel by integer coordinates, as Load reads it: 0 outside the texture.</summary>
    public static float4 Load(CpuTexture texture, int level, int x, int y, int z)
    {
        if (level < 0 || level >= texture.MipLevels || x < 0 || y < 0 || z < 0
            || x >= texture.LevelWidth(level) || y >= texture.LevelHeight(level) || z >= texture.LevelDepth(level))
            return default;
        return texture.Read(level, x, y, z);
    }
}
