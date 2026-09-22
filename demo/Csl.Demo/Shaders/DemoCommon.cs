using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// Shared by call: functions any shader calls as <c>DemoCommon.Fbm(p)</c>, without inheriting or mixing
/// anything in, the way the engine's LuminanceUtils.Luma is called. Static in C#, static in SDSL.
/// </summary>
[Shader]
public abstract partial class DemoCommon
{
    /// <summary>A pseudo-random number in [0, 1) for a point.</summary>
    public static float Hash(float2 p)
    {
        return frac(sin(dot(p, new float2(127.1f, 311.7f))) * 43758.5453f);
    }

    /// <summary>Value noise: the hashes of the four corners of p's cell, smoothly interpolated.</summary>
    public static float Noise(float2 p)
    {
        float2 cell = floor(p);
        float2 f = frac(p);
        float2 u = f * f * (3.0f - 2.0f * f);
        float a = Hash(cell);
        float b = Hash(cell + new float2(1.0f, 0.0f));
        float c = Hash(cell + new float2(0.0f, 1.0f));
        float d = Hash(cell + new float2(1.0f, 1.0f));
        return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
    }

    /// <summary>Fractal noise: five octaves of Noise, each twice the frequency and half the amplitude.</summary>
    public static float Fbm(float2 p)
    {
        float sum = 0.0f;
        float amplitude = 0.5f;
        for (int i = 0; i < 5; i++)
        {
            sum += amplitude * Noise(p);
            p = p * 2.0f;
            amplitude *= 0.5f;
        }
        return sum;
    }

    /// <summary>A colour for t, cycling: one cosine per channel, each a third of a turn apart.</summary>
    public static float3 Palette(float t)
    {
        return 0.5f + 0.5f * cos(6.28318f * (t + new float3(0.0f, 0.33f, 0.67f)));
    }
}
