using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Csl.Tests.Samples;

/// <summary>
/// One thread per brick: a brick is active this step when it, or one of its six neighbours,
/// changed on the previous step (or was marked from the CPU by a dig or a pour). Water crosses
/// at most one cell per step, so a change can only reach the next brick over.
/// </summary>
[Shader, NumThreads(4, 4, 4)]
public partial class SampleSpread : ComputeShaderBase
{
    [Stage] public Texture3D<uint> ChangedBricks;
    [Stage] public RWTexture3D<uint> ActiveOut;

    /// <summary>
    /// Every brick that was active on any sub-step of this step, for the passes that draw the field
    /// once after them all. Reset on the first sub-step, or a brick that settled on it would be
    /// dropped by the last spread and drawn from stale amounts.
    /// </summary>
    [Stage] public RWTexture3D<uint> DrawnOut;
    [Stage] public int Reset;

    [Stage] public int3 BrickCount;

    uint Changed(int3 b)
    {
        if (any(b < 0) || any(b >= BrickCount))
            return 0;
        return ChangedBricks.Load(new int4(b, 0));
    }

    public override void Compute()
    {
        int3 b = (int3)DispatchThreadId;
        if (any(b >= BrickCount))
            return;
        uint active = Changed(b)
            | Changed(b + new int3(1, 0, 0)) | Changed(b - new int3(1, 0, 0))
            | Changed(b + new int3(0, 1, 0)) | Changed(b - new int3(0, 1, 0))
            | Changed(b + new int3(0, 0, 1)) | Changed(b - new int3(0, 0, 1));
        uint on = active != 0 ? 1u : 0u;
        ActiveOut[b] = on;
        DrawnOut[b] = Reset != 0 ? on : (DrawnOut[b] | on);
    }
}
