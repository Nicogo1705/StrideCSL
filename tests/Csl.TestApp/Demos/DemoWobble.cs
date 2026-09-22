using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.TestApp.Demos;

/// <summary>
/// Reads a texture: Texture0 (a checkerboard the app makes) sampled at coordinates bent by a wave.
/// </summary>
[Shader, Mixin(typeof(Global))]
public partial class DemoWobble : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        float2 uv = streams.TexCoord;
        uv.x += 0.03f * sin(uv.y * 20.0f + Time * 3.0f);
        uv.y += 0.03f * cos(uv.x * 20.0f + Time * 2.0f);
        return Texture0.Sample(LinearRepeatSampler, uv * 2.0f);
    }
}
