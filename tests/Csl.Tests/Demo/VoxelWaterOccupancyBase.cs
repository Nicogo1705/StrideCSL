// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Demo;

/// <summary>
/// The occupancy pyramid's base level from the composed field, for the bricks that changed: each
/// entry is the least and greatest density of a 2x2x2-cell brick, over its 3x3x3 samples (bricks
/// overlap by one sample, a cell's surface depending on its eight corners). Rounded outwards, so
/// a brick never claims to be emptier than it is. See VoxelGridOccupancy.
/// </summary>
[Shader, NumThreads(4, 4, 4), Mixin(typeof(VoxelWaterBricks))]
public partial class VoxelWaterOccupancyBase : ComputeShaderBase
{
    /// <summary>The field's finest level, and the pyramid's mip 0.</summary>
    [Stage] public Texture3D<float2> Field;
    [Stage] public RWTexture3D<float2> Target;
    [Stage] public int3 TargetSize;

    public override void Compute()
    {
        int3 b = (int3)DispatchThreadId;
        if (any(b >= TargetSize))
            return;
        int3 first = b * 2;
        if (!BoxDirty(first, first + 2))
            return;

        float low = 1.0f;
        float high = 0.0f;
        Unroll();
        for (int z = 0; z <= 2; z++)
        {
            Unroll();
            for (int y = 0; y <= 2; y++)
            {
                Unroll();
                for (int x = 0; x <= 2; x++)
                {
                    int3 s = first + new int3(x, y, z);
                    float value = all(s < SampleCount) ? Field.Load(new int4(s, 0)).r : 0.0f;
                    low = min(low, value);
                    high = max(high, value);
                }
            }
        }
        Target[b] = new float2(floor(low * 255.0f) / 255.0f, ceil(high * 255.0f) / 255.0f);
    }
}
