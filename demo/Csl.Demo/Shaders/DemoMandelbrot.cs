using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// The Mandelbrot set, zooming in and out: a method with a loop and a break, called four times per
/// pixel so the edge of the set, finer than a pixel, does not sparkle.
/// </summary>
[Shader, Mixin(typeof(Global))]
public partial class DemoMandelbrot : ImageEffectShader
{
    /// <summary>The colour of one point: black inside the set, lighter the longer it takes to escape.</summary>
    public float3 Escape(float2 c)
    {
        float2 z = new float2(0.0f, 0.0f);
        int steps = 0;
        for (int i = 0; i < 128; i++)
        {
            if (dot(z, z) > 4.0f)
                break;
            z = new float2(z.x * z.x - z.y * z.y, 2.0f * z.x * z.y) + c;
            steps++;
        }
        float3 outside = lerp(new float3(0.05f, 0.0f, 0.15f), new float3(0.85f, 0.35f, 0.9f), sqrt(steps / 128.0f));
        return steps == 128 ? new float3(0.0f, 0.0f, 0.0f) : outside;
    }

    [Stage]
    public override float4 Shading()
    {
        float zoom = 1.5f * exp(-2.5f * (0.5f - 0.5f * cos(Time * 0.3f)));
        float2 center = new float2(-0.745f, 0.186f);
        float2 c = center + (streams.TexCoord - 0.5f) * 2.0f * zoom;
        // A quarter of a pixel on each axis: four samples inside the pixel, averaged.
        float2 quarter = new float2(ddx(c.x), ddy(c.y)) * 0.25f;
        float3 color = Escape(c + new float2(-quarter.x, -quarter.y))
                     + Escape(c + new float2(quarter.x, -quarter.y))
                     + Escape(c + new float2(-quarter.x, quarter.y))
                     + Escape(c + quarter);
        return new float4(color * 0.25f, 1.0f);
    }
}
