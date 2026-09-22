using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// Calls an engine shader: the checkerboard of Texture0 turned grey by LuminanceUtils.Luma (Csl.Engine
/// has every engine shader as a C# class to call, inherit or mix in), a sweep going across.
/// </summary>
[Shader, Mixin(typeof(Global))]
public partial class DemoLuma : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        float2 uv = streams.TexCoord;
        float4 color = Texture0.Sample(PointSampler, uv);
        float grey = LuminanceUtils.Luma(color.rgb);
        // Colour left of the sweep, grey right of it.
        float sweep = 0.5f + 0.45f * sin(Time);
        float3 result = uv.x < sweep ? color.rgb : new float3(grey, grey, grey);
        return new float4(result, 1.0f);
    }
}
