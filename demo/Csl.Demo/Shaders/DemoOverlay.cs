using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// The checkerboard of Texture0 over a moving gradient, blended by the engine's BlendUtils.Overlay:
/// not static, so BlendUtils is mixed in and Overlay is called as the shader's own method.
/// </summary>
[Shader, Mixin(typeof(BlendUtils))]
public partial class DemoOverlay : DemoTile
{
    public override float3 Color(float2 p)
    {
        float4 checker = Texture0.Sample(LinearRepeatSampler, streams.TexCoord * 2.0f);
        float4 gradient = new float4(DemoCommon.Palette(p.x * 0.4f + Time * 0.2f), 1.0f);
        return Overlay(gradient, checker).rgb;
    }
}
