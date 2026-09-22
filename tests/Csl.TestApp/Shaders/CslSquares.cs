using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Csl.TestApp.Shaders;

/// <summary>A shader written in C# from scratch: one thread per element, <c>Output[i] = i * i + Offset</c>.</summary>
[Shader, NumThreads(64)]
public partial class CslSquares : ComputeShaderBase
{
    [Stage] public RWBuffer<uint> Output;
    [Stage] public uint Offset;

    public override void Compute()
    {
        uint i = streams.DispatchThreadId.x;
        Output[i] = i * i + Offset;
    }
}
