using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>The Mandelbrot set, zooming in and out: a loop with a break.</summary>
[Shader, Mixin(typeof(Global))]
public partial class DemoMandelbrot : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        float zoom = 1.5f * exp(-2.5f * (0.5f - 0.5f * cos(Time * 0.3f)));
        float2 center = new float2(-0.745f, 0.186f);
        float2 c = center + (streams.TexCoord - 0.5f) * 2.0f * zoom;
        float2 z = new float2(0.0f, 0.0f);
        int steps = 0;
        for (int i = 0; i < 128; i++)
        {
            if (dot(z, z) > 4.0f)
                break;
            z = new float2(z.x * z.x - z.y * z.y, 2.0f * z.x * z.y) + c;
            steps++;
        }
        if (steps == 128)
            return new float4(0.0f, 0.0f, 0.0f, 1.0f);
        float s = steps / 128.0f;
        return new float4(sqrt(s), s * s, 0.3f + 0.7f * s, 1.0f);
    }
}
