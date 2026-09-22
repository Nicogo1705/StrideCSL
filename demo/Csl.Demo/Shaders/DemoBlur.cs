using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// A compute shader over the whole window: the tiles are drawn into Input, this blurs them into
/// Output, one thread per pixel. A gaussian over the (2 Radius + 1)² pixels around; B turns it on and
/// off, + and - change Radius. Size and Radius are set by the app through the generated DemoBlurEffect.
/// </summary>
[Shader, NumThreads(8, 8, 1)]
public partial class DemoBlur : ComputeShaderBase
{
    [Stage] public Texture2D<float4> Input;
    [Stage] public RWTexture2D<float4> Output;
    [Stage] public int2 Size;
    [Stage] public int Radius;

    public override void Compute()
    {
        uint2 p = streams.DispatchThreadId.xy;
        if (p.x < (uint)Size.x && p.y < (uint)Size.y)
        {
            float sigma = max(Radius * 0.5f, 0.5f);
            float4 sum = new float4(0.0f, 0.0f, 0.0f, 0.0f);
            float total = 0.0f;
            for (int dy = -Radius; dy <= Radius; dy++)
            {
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    int2 at = clamp(new int2((int)p.x + dx, (int)p.y + dy), new int2(0, 0), Size - 1);
                    float weight = exp(-(dx * dx + dy * dy) / (2.0f * sigma * sigma));
                    sum += Input[at] * weight;
                    total += weight;
                }
            }
            Output[p] = sum / total;
        }
    }
}
