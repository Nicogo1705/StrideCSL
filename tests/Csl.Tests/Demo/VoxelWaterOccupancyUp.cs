// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Demo;

/// <summary>
/// One level of the occupancy pyramid from the level under it: each brick is the union of its
/// eight children, which the one-sample overlap makes exact. Only for the bricks whose samples
/// touch an active brick.
/// </summary>
[Shader, NumThreads(4, 4, 4), Mixin(typeof(VoxelWaterBricks))]
public partial class VoxelWaterOccupancyUp : ComputeShaderBase
{
    /// <summary>Single-mip views: the finer level to read, this level to write.</summary>
    [Stage] public Texture3D<float2> Source;
    [Stage] public RWTexture3D<float2> Target;
    [Stage] public int3 SourceSize;
    [Stage] public int3 TargetSize;

    /// <summary>Cells along a brick's edge at this level: 2^(mip + 1).</summary>
    [Stage] public int BrickCells;

    public override void Compute()
    {
        int3 b = (int3)DispatchThreadId;
        if (any(b >= TargetSize))
            return;
        int3 first = b * BrickCells;
        if (!BoxDirty(first, first + BrickCells))
            return;

        float low = 1.0f;
        float high = 0.0f;
        Unroll();
        for (int z = 0; z <= 1; z++)
        {
            Unroll();
            for (int y = 0; y <= 1; y++)
            {
                Unroll();
                for (int x = 0; x <= 1; x++)
                {
                    int3 child = min(b * 2 + new int3(x, y, z), SourceSize - 1);
                    float2 value = Source.Load(new int4(child, 0));
                    low = min(low, value.r);
                    high = max(high, value.g);
                }
            }
        }
        Target[b] = new float2(low, high);
    }
}
