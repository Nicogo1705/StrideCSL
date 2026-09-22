// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;

namespace Demo;

/// <summary>
/// Writes the drawn field's finest level for the active bricks: the dry terrain where it is denser,
/// the water elsewhere. One thread per cell, one group per brick.
///
/// The water's density is laid out so the iso surface lands where the water stands: a partial cell
/// puts the surface inside itself at its fraction, a full cell reads the cell above for where the
/// surface is, an empty cell the cell below - the same ramp of half a density per cell that a
/// column of water at a known level would have, so the trilinear surface comes out flat and level.
/// </summary>
[Shader, NumThreads(8, 8, 8), Mixin(typeof(VoxelWaterBricks))]
public partial class VoxelWaterCompose : ComputeShaderBase
{
    [Stage] public Texture3D<float2> Terrain;
    [Stage] public Texture3D<float> Amounts;
    [Stage] public RWTexture3D<float2> FieldOut;

    /// <summary>The water's material id, as the texture stores it: id / 255.</summary>
    [Stage] public float WaterMaterial;

    /// <summary>
    /// A cell holding at least FilmMin, with less than a full cell under it, is drawn as a film at
    /// least FilmDensity dense: the ramp alone puts a thin layer's surface under its own sample,
    /// inside whatever is below, so a trickle down a slope was invisible until it pooled, and the
    /// rim of a spreading sheet flickered in and out across the iso level.
    /// </summary>
    [Stage] public float FilmMin;
    [Stage] public float FilmDensity;

    float Amount(int3 c)
    {
        if (any(c < 0) || any(c >= SampleCount))
            return 0.0f;
        return Amounts.Load(new int4(c, 0));
    }

    /// <summary>Height of the water surface above this cell's sample, in cells, from the cell and its vertical neighbours.</summary>
    float SurfaceOffset(int3 c, float f)
    {
        if (f >= 1.0f)
        {
            float above = Amount(c + new int3(0, 1, 0));
            if (above >= 1.0f)
                return 2.0f;
            if (above > 0.0f)
                return 0.5f + above;
            return 0.5f;
        }
        if (f > 0.0f)
            return f - 0.5f;
        float below = Amount(c - new int3(0, 1, 0));
        if (below >= 1.0f)
            return -0.5f;
        if (below > 0.0f)
            return below - 1.5f;
        return -2.0f;
    }

    public override void Compute()
    {
        int3 brick = (int3)GroupId;
        if (!BrickActive(brick))
            return;
        int3 c = (int3)DispatchThreadId;
        if (any(c >= SampleCount))
            return;

        float2 terrain = Terrain.Load(new int4(c, 0));
        float f = Amount(c);
        float water = saturate(0.5f + 0.5f * SurfaceOffset(c, f));
        if (f >= FilmMin && f < 1.0f && Amount(c - new int3(0, 1, 0)) < 1.0f)
            water = max(water, FilmDensity);
        // Never under what the terrain already holds there, and nothing for a trace that would not draw.
        if (water > terrain.r && water >= 8.0f / 255.0f)
            FieldOut[c] = new float2(water, WaterMaterial);
        else
            FieldOut[c] = terrain;
    }
}
