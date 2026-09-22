using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Csl.TestApp.Shaders;

/// <summary>
/// An engine shader extended from C#: ColorUtility (Csl.Engine) mixed in, its ToLinear called with
/// the types C# checks. <c>Output[i] = ToLinear(i / 63)</c>.
/// </summary>
[Shader, NumThreads(64), Mixin(typeof(ColorUtility))]
public partial class CslColorLinear : ComputeShaderBase
{
    [Stage] public RWBuffer<float> Output;

    public override void Compute()
    {
        uint i = streams.DispatchThreadId.x;
        Output[i] = ToLinear(i / 63.0f);
    }
}
