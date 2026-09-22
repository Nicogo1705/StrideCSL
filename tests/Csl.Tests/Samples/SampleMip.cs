using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Csl.Tests.Samples;

/// <summary>
/// One level of the field's mip chain from the level above it, for the bricks whose sources changed.
/// Sample j stands over sample 2j of the finer level, filtered over its two neighbours along each
/// axis (weights 2 and 1) so the surface smooths rather than aliases and keeps its place; the
/// material is the centre sample's. One thread per cell of this level, one group per 8x8x8 brick.
/// </summary>
[Shader, NumThreads(8, 8, 8), Mixin(typeof(SampleBricks))]
public partial class SampleMip : ComputeShaderBase
{
    /// <summary>Single-mip views: the finer level to read, this level to write.</summary>
    [Stage] public Texture3D<float2> Source;
    [Stage] public RWTexture3D<float2> Target;
    [Stage] public int3 SourceSize;
    [Stage] public int3 TargetSize;

    /// <summary>Which mip this is, 1 for the first below the field.</summary>
    [Stage] public int Level;

    public override void Compute()
    {
        // The brick's cells at this level, widened by one at every level down for the filter's reach,
        // give the level-0 box this brick depends on.
        int3 brick = (int3)GroupId;
        int3 lo = brick * 8;
        int3 hi = lo + 7;
        Loop();
        for (int i = 0; i < Level; i++)
        {
            lo = lo * 2 - 1;
            hi = hi * 2 + 1;
        }
        if (!BoxDirty(lo, hi))
            return;

        int3 c = (int3)DispatchThreadId;
        if (any(c >= TargetSize))
            return;

        int3 centre = min(c * 2, SourceSize - 1);
        float sum = 0.0f;
        float weight = 0.0f;
        Unroll();
        for (int dz = -1; dz <= 1; dz++)
        {
            Unroll();
            for (int dy = -1; dy <= 1; dy++)
            {
                Unroll();
                for (int dx = -1; dx <= 1; dx++)
                {
                    int3 s = centre + new int3(dx, dy, dz);
                    if (any(s < 0) || any(s >= SourceSize))
                        continue;
                    float w = (dx == 0 ? 2.0f : 1.0f) * (dy == 0 ? 2.0f : 1.0f) * (dz == 0 ? 2.0f : 1.0f);
                    sum += Source.Load(new int4(s, 0)).r * w;
                    weight += w;
                }
            }
        }
        Target[c] = new float2(sum / weight, Source.Load(new int4(centre, 0)).g);
    }
}
