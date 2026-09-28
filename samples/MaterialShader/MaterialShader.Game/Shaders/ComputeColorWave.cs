// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Csl;
using Csl.Types;
using Csl.Engine;
using static Csl.Types.Intrinsics;

namespace MaterialShader.Shaders;

[Shader]
[Mixin(typeof(Texturing))]
public abstract partial class ComputeColorWave : ComputeColor
{
    [Generic] public static float Frequency;
    [Generic] public static float Amplitude;
    [Generic] public static float Speed;

    public override float4 Compute()
    {
        float phase = length(streams.TexCoord - 0.5f);
        return sin((phase + Sdsl.Static<Global>().Time * Speed) * 2 * 3.14f * Frequency) * Amplitude;
    }
}
