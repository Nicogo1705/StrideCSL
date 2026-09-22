// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Csl;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Demo;

/// <summary>
/// The water lives in bricks of 8x8x8 cells. A brick is active for one step when something in it
/// or next to it changed on the previous step; every pass that rebuilds something derived from
/// the field asks here whether the level-0 box it depends on touches an active brick.
/// </summary>
[Shader]
public partial class VoxelWaterBricks
{
    /// <summary>One byte per brick, non-zero where the brick is simulated and recomposed this step.</summary>
    [Stage] public Texture3D<uint> ActiveBricks;

    /// <summary>Samples per axis of the field, and bricks per axis (samples / 8, rounded up).</summary>
    [Stage] public int3 SampleCount;
    [Stage] public int3 BrickCount;

    /// <summary>Whether any active brick overlaps the inclusive box [lo, hi] of level-0 samples.</summary>
    public bool BoxDirty(int3 lo, int3 hi)
    {
        int3 b0 = max(lo, new int3(0, 0, 0)) >> 3;
        int3 b1 = min(max(hi, new int3(0, 0, 0)) >> 3, BrickCount - 1);
        Loop();
        for (int z = b0.z; z <= b1.z; z++)
        {
            Loop();
            for (int y = b0.y; y <= b1.y; y++)
            {
                Loop();
                for (int x = b0.x; x <= b1.x; x++)
                {
                    if (ActiveBricks.Load(new int4(x, y, z, 0)) != 0)
                        return true;
                }
            }
        }
        return false;
    }

    public bool BrickActive(int3 brick)
    {
        return ActiveBricks.Load(new int4(brick, 0)) != 0;
    }
}
