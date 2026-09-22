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
    [Stage] public RWStructuredBuffer<float> Output;

    public override void Compute()
    {
        uint i = streams.DispatchThreadId.x;
        // The channels as uint locals: see EngineProbeDivision for why not in the constructor.
        uint r = i % 4;
        uint g = i / 4 % 4;
        uint b = i / 16 % 4;
        float3 color = new float3(r, g, b) / 3.0f;
        Output[i] = LuminanceUtils.Luma(color);
    }
}
