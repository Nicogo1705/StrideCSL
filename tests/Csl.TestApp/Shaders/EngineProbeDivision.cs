using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.TestApp.Shaders;

/// <summary>
/// Not a test of the C# shaders: a probe of the engine's SDSL compiler. <c>float3(i / 4, 0, 0)</c>
/// with a uint i divides as integers in HLSL (5 / 4 = 1); Output.x is that, Output.y the same
/// division through a local, for comparison.
/// </summary>
[Shader, NumThreads(64)]
public partial class EngineProbeDivision : ComputeShaderBase
{
    [Stage] public RWStructuredBuffer<float2> Output;

    public override void Compute()
    {
        uint i = streams.DispatchThreadId.x;
        uint quotient = i / 4;
        Output[i] = new float2(new float3(i / 4, 0, 0).x, quotient);
    }
}
