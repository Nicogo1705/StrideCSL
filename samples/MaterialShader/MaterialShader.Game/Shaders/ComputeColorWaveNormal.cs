// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Csl;
using Csl.Types;
using Csl.Engine;
using static Csl.Types.Intrinsics;

namespace MaterialShader.Shaders;

[Shader]
[Mixin(typeof(Texturing))]
public abstract partial class ComputeColorWaveNormal : ComputeColor
{
    [Generic] public static float Frequency;
    [Generic] public static float Amplitude;
    [Generic] public static float Speed;

    public override float4 Compute()
    {
        float2 offset = streams.TexCoord - 0.5f;
        float phase = length(offset);

        float derivative = cos((phase + Sdsl.Static<Global>().Time * Speed) * 2 * 3.14f * Frequency) * Amplitude;

        float2 xz = SincosOfAtan(offset.y / offset.x);
        float2 xy = SincosOfAtan(derivative);

        float3 normal = default;
        normal.xy = (xz.yx * sign(offset.x) * -xy.x) * 0.5f + 0.5f;
        normal.z = xy.y;
        return new float4(normal, 1);
    }

    public virtual float2 SincosOfAtan(float x)
    {
        return new float2(x, 1) / sqrt(1 + x * x);
    }
}
