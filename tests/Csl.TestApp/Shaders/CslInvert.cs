using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.TestApp.Shaders;

/// <summary>
/// A graphics shader extended from C#: the engine's ImageEffectShader (a sprite drawn over the
/// output, sampling Texture0), its Shading overridden to invert the colour.
/// </summary>
[Shader]
public partial class CslInvert : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        float4 color = Texture0.Sample(PointSampler, streams.TexCoord);
        return new float4(1.0f - color.rgb, color.a);
    }
}
