using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Csl.TestApp.Shaders;

/// <summary>
/// Calls the engine's LuminanceUtils.Luma, which Modified/LuminanceUtils.cs replaces: the result
/// shows which of the two the effect compiler used. 64 colours, one channel per 2 bits of i.
/// </summary>
[Shader, NumThreads(64)]
public partial class CslLuma : ComputeShaderBase
{
    [Stage] public RWBuffer<float> Output;

    public override void Compute()
    {
        uint i = streams.DispatchThreadId.x;
        float3 color = new float3(i % 4, i / 4 % 4, i / 16 % 4) / 3.0f;
        Output[i] = LuminanceUtils.Luma(color);
    }
}
