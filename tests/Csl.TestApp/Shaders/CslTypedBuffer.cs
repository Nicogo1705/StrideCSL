using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Csl.TestApp.Shaders;

/// <summary>A typed buffer written through its unordered access view: <c>Output[i] = i * 3</c>. Needs feature level 11_0 on Direct3D 11.</summary>
[Shader, NumThreads(64)]
public partial class CslTypedBuffer : ComputeShaderBase
{
    [Stage] public RWBuffer<uint> Output;

    public override void Compute()
    {
        uint i = streams.DispatchThreadId.x;
        Output[i] = i * 3;
    }
}
