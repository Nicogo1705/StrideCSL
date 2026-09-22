using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>Rings moving out from the centre: the distance to it, through a sine.</summary>
[Shader, Mixin(typeof(Global))]
public partial class DemoRings : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        float2 p = streams.TexCoord - 0.5f;
        float d = length(p);
        float wave = 0.5f + 0.5f * sin(d * 60.0f - Time * 5.0f);
        // Brighter near the centre, fading out at the edge.
        float fade = saturate(1.0f - d * 1.6f);
        float3 color = lerp(new float3(0.05f, 0.1f, 0.3f), new float3(1.0f, 0.8f, 0.3f), wave) * fade;
        return new float4(color, 1.0f);
    }
}
