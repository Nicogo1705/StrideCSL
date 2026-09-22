using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// A plasma: a few sines summed, coloured by a cosine palette (a method of the shader, called from
/// Shading as in SDSL).
/// </summary>
[Shader, Mixin(typeof(Global))]
public partial class DemoPlasma : ImageEffectShader
{
    /// <summary>A colour for t, cycling: one cosine per channel, each a third of a turn apart.</summary>
    public float3 Palette(float t)
    {
        return 0.5f + 0.5f * cos(6.28318f * (t + new float3(0.0f, 0.33f, 0.67f)));
    }

    [Stage]
    public override float4 Shading()
    {
        float2 p = streams.TexCoord * 8.0f;
        float t = Time;
        float v = sin(p.x + t)
                + sin((p.y + t) * 0.5f)
                + sin((p.x + p.y + t) * 0.5f)
                + sin(length(p - 4.0f) - t * 1.5f);
        return new float4(Palette(v * 0.25f + t * 0.1f), 1.0f);
    }
}
