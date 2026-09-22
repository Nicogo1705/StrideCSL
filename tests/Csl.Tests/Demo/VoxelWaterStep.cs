// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Demo;

/// <summary>
/// One flow step of the water, one thread per cell, one group per brick; inactive bricks return at once.
///
/// The water is an amount per cell, nominally up to one, a little over under pressure. Each step every
/// cell hands some of its water to its neighbours: down first, as much as the cell below can take; then
/// sideways, an eighth of the difference to each lower neighbour, out of what is left; then up, whatever
/// exceeds the resting share of the pair with the cell above - which is how a column pushes water back
/// up the other side of a bend. Every hand-over is a pure function of the two cells' amounts and of
/// the sender's own neighbourhood, so the receiver computes exactly the same number as the sender
/// and nothing is created or lost: this is a gather, written to a second texture, no atomics.
///
/// The bricks that were not written keep equal contents in both textures, which is what lets the
/// two swap without a copy: a brick is written whenever it or a neighbour changed, and a brick that
/// nobody wrote holds, in both, the value it settled on.
/// </summary>
[Shader, NumThreads(8, 8, 8), Mixin(typeof(VoxelWaterBricks))]
public partial class VoxelWaterStep : ComputeShaderBase
{
    /// <summary>Set to one for the group's brick when any of its cells changed, cleared otherwise.</summary>
    [Stage] public RWTexture3D<uint> ChangedOut;

    /// <summary>The dry field: density in red. A sample at or over the iso level is solid and holds no water.</summary>
    [Stage] public Texture3D<float2> Terrain;

    /// <summary>Amounts read this step, and where the new ones go.</summary>
    [Stage] public Texture3D<float> Amounts;
    [Stage] public RWTexture3D<float> AmountsOut;

    [Stage] public float IsoLevel;

    /// <summary>How much a cell can hold over one, per cell of water above it; the pressure that lifts water.</summary>
    [Stage] public float MaxCompress;

    /// <summary>
    /// Amounts and hand-overs are multiples of this (a power of two, exact in a half float), so a
    /// sheet at rest is exactly still: no crumb is traded forever, and a brick that nobody wrote
    /// holds the same bits in both textures.
    /// </summary>
    [Stage] public float Quantum;

    /// <summary>A film thinner than this soaks away.</summary>
    [Stage] public float Soak;

    /// <summary>Water poured this step: a ball in cell units, and how much each cell inside gains. Radius zero for none.</summary>
    [Stage] public float3 PourCentre;
    [Stage] public float PourRadius;
    [Stage] public float PourAmount;

    [GroupShared] uint anyChanged;

    bool Solid(int3 c)
    {
        if (any(c < 0) || any(c >= SampleCount))
            return true;
        return Terrain.Load(new int4(c, 0)).r >= IsoLevel;
    }

    float Amount(int3 c)
    {
        if (any(c < 0) || any(c >= SampleCount))
            return 0.0f;
        return Amounts.Load(new int4(c, 0));
    }

    /// <summary>What the lower cell of a vertical pair holds at rest, given the pair's total.</summary>
    float StableBelow(float total)
    {
        if (total <= 1.0f)
            return 1.0f;
        if (total < 2.0f + MaxCompress)
            return (1.0f + total * MaxCompress) / (1.0f + MaxCompress);
        return (total + MaxCompress) * 0.5f;
    }

    float Quantized(float flow)
    {
        return floor(flow / Quantum + 1e-4f) * Quantum;
    }

    /// <summary>Water a sender at s holding a hands to the cell below it.</summary>
    float Down(int3 s, float a)
    {
        int3 below = s - new int3(0, 1, 0);
        if (Solid(below))
            return 0.0f;
        float b = Amount(below);
        return Quantized(clamp(StableBelow(a + b) - b, 0.0f, a));
    }

    /// <summary>
    /// Water a sender holding rem (after its downward hand-over) hands to a lateral neighbour n:
    /// a fifth of the difference - the most four neighbours can take at once while the sender
    /// keeps a share, which is what stops a checkerboard - and never under one quantum, so two
    /// neighbours end within a quantum of each other rather than a fifth apart.
    /// </summary>
    float Lateral(int3 n, float rem)
    {
        if (Solid(n))
            return 0.0f;
        float diff = rem - Amount(n);
        if (diff < 2.0f * Quantum)
            return 0.0f;
        return Quantized(min(max(diff * 0.2f, Quantum), rem * 0.25f));
    }

    /// <summary>What is left in a sender after down and sideways, from which the upward hand-over is taken.</summary>
    float Remaining(int3 s, float a)
    {
        float rem = a - Down(s, a);
        float lateral = Lateral(s + new int3(1, 0, 0), rem) + Lateral(s - new int3(1, 0, 0), rem)
                      + Lateral(s + new int3(0, 0, 1), rem) + Lateral(s - new int3(0, 0, 1), rem);
        return rem - lateral;
    }

    /// <summary>Water a sender at s with rem2 left hands to the cell above it.</summary>
    float Up(int3 s, float rem2)
    {
        int3 above = s + new int3(0, 1, 0);
        if (Solid(above))
            return 0.0f;
        float u = Amount(above);
        return Quantized(clamp(rem2 - StableBelow(rem2 + u), 0.0f, rem2));
    }

    public override void Compute()
    {
        int3 brick = (int3)GroupId;
        if (!BrickActive(brick))
            return;

        if (GroupIndex == 0)
            anyChanged = 0;
        GroupMemoryBarrierWithGroupSync();

        int3 c = (int3)DispatchThreadId;
        if (all(c < SampleCount))
        {
            float a = Amount(c);
            float value;
            if (Solid(c))
            {
                // Earth filled in over water: the water is gone, not lifted.
                value = 0.0f;
            }
            else
            {
                // Out: the three hand-overs, in order, each from what the one before left.
                float down = Down(c, a);
                float rem = a - down;
                int3 xp = c + new int3(1, 0, 0), xm = c - new int3(1, 0, 0);
                int3 zp = c + new int3(0, 0, 1), zm = c - new int3(0, 0, 1);
                float lateral = Lateral(xp, rem) + Lateral(xm, rem) + Lateral(zp, rem) + Lateral(zm, rem);
                float rem2 = rem - lateral;
                float up = Up(c, rem2);

                // In: what the cell above lets fall, what the four beside hand across, what the one below pushes up.
                int3 above = c + new int3(0, 1, 0), below = c - new int3(0, 1, 0);
                float inflow = 0.0f;
                if (!Solid(above))
                    inflow += Down(above, Amount(above));
                if (!Solid(xp)) inflow += Lateral(c, Amount(xp) - Down(xp, Amount(xp)));
                if (!Solid(xm)) inflow += Lateral(c, Amount(xm) - Down(xm, Amount(xm)));
                if (!Solid(zp)) inflow += Lateral(c, Amount(zp) - Down(zp, Amount(zp)));
                if (!Solid(zm)) inflow += Lateral(c, Amount(zm) - Down(zm, Amount(zm)));
                if (!Solid(below))
                    inflow += Up(below, Remaining(below, Amount(below)));

                value = round((rem2 - up + inflow) / Quantum) * Quantum;
                if (value < Soak)
                    value = 0.0f;

                if (PourRadius > 0.0f)
                {
                    float3 d = (float3)c - PourCentre;
                    if (dot(d, d) <= PourRadius * PourRadius)
                        value = min(1.0f, value + PourAmount);
                }
            }

            AmountsOut[c] = value;
            if (value != a)
                InterlockedOr(ref anyChanged, 1);
        }

        GroupMemoryBarrierWithGroupSync();
        if (GroupIndex == 0)
            ChangedOut[brick] = anyChanged;
    }
}
