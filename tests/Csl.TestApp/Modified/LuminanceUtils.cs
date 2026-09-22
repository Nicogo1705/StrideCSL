// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Csl;
using Csl.Hlsl;
using Csl.Engine;
using static Csl.Hlsl.Intrinsics;

namespace Csl.TestApp.Modified;

/// <summary>
/// A utility shader for luminance.
/// </summary>
/// <remarks>
/// The engine's LuminanceUtils, converted by <c>csl convert --engine LuminanceUtils</c> and then
/// modified: Luma is the brightest channel here, not the 601 weights. Same SDSL name, so once
/// registered this replaces the engine's shader for every effect that uses it (see the gpu tests).
/// </remarks>
[Shader]
public abstract partial class LuminanceUtils
{
    /// <summary>
    /// Calculate the perceptive luminance (601Y')
    /// </summary>
    /// <remarks>
    /// http://en.wikipedia.org/wiki/HSL_and_HSV#Lightness
    /// http://www.poynton.com/PDFs/YUV_and_luminance_harmful.pdf
    /// </remarks>
    public static float Luma(float3 color)
    {
        // Modified: the engine returns max(dot(color, float3(0.299, 0.587, 0.114)), 0.0001).
        return max(color.r, max(color.g, color.b));
    }
}
