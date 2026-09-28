using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.TestApp.Shaders;

/// <summary>
/// What the GPU's arithmetic gives, to measure the CPU run against: per thread, an argument x read
/// from Input (the same bits on both), and one column per operation. Offset is a parameter so that
/// nothing is folded at compile time.
/// </summary>
[Shader, NumThreads(64)]
public partial class CslPrecision : ComputeShaderBase
{
    public const int Columns = 16;

    [Stage] public RWStructuredBuffer<float> Output;
    [Stage] public StructuredBuffer<float> Input;
    [Stage] public float Offset;

    public override void Compute()
    {
        uint i = streams.DispatchThreadId.x;
        float x = Input[i];

        uint o = i * Columns;
        Output[o + 0] = x;
        Output[o + 1] = sin(x);
        Output[o + 2] = cos(x);
        Output[o + 3] = exp(x / 1000.0f);
        Output[o + 4] = log(abs(x));
        Output[o + 5] = rsqrt(abs(x));
        Output[o + 6] = frac(sin(dot(new float2(x, x * 0.5f), new float2(127.1f, 311.7f))) * 43758.5453f);
        Output[o + 7] = pow(abs(x), 1.7f);
        // (1 + 2^-12)² = 1 + 2^-11 + 2^-24, whose last term a 32-bit product drops: a*a-1 is 2^-11
        // unfused, 2^-11 + 2^-24 fused (mad as an FMA).
        float a = 1.0f + Offset;
        Output[o + 8] = a * a - 1.0f;
        Output[o + 9] = x * 0.1f + 1.0f / 3.0f;
        // Components from unrelated inputs: the compiler cannot factor them.
        float y = Input[(i + 37) % 512];
        float z = Input[(i + 101) % 512];
        Output[o + 10] = dot(new float2(x, y), new float2(127.1f, 311.7f));
        Output[o + 11] = dot(new float3(x, y, z), new float3(127.1f, 311.7f, 74.7f));
        Output[o + 12] = lerp(x, y, frac(z));
        Output[o + 13] = smoothstep(0.0f, 1.0f, frac(x));
        Output[o + 14] = normalize(new float3(x, y, z)).x;
        Output[o + 15] = length(new float3(x, y, z));
    }
}
