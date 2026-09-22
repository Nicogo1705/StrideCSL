using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.TestApp.Demos;

/// <summary>
/// The simplest one: red across, green down, blue pulsing with time. Start here: change a channel,
/// save, and the tile changes while "gpu" runs.
/// </summary>
[Shader, Mixin(typeof(Global))]
public partial class DemoGradient : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        float2 uv = streams.TexCoord;
        float pulse = 0.5f + 0.5f * sin(Time * 2.0f);
        return new float4(uv.x, uv.y, pulse, 1.0f);
    }
}
