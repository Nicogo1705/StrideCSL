using System;
using System.Threading;

namespace Csl.Types;

/// <summary>
/// The intrinsics that are not plain maths: group synchronisation, atomics, and the loop attribute
/// markers. The maths is in Intrinsics.g.cs.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public static partial class Intrinsics
{
    // -- transcendentals as the GPU computes them ------------------------------------------------------
    // The hardware takes sin and cos in turns: x times the 32-bit 1 / 2π, rounded toward zero, of which
    // the unit keeps the fraction (measured by Csl.TestApp gpu, CslPrecision: then within 1e-7 of the
    // GPU). A large argument loses what a float cannot hold, and the frac(sin(x) * 43758.5) hash depends
    // on exactly what. exp and log go through exp2 and log2, their argument scaled in 32 bits.

    /// <summary>sin, cos, tan, exp and log with the GPU's argument reduction (true, the default), or exact.</summary>
    public static bool GpuTranscendentals = true;

    private const float InverseTwoPi = 0.159154943f;

    private static double Turns(float v)
    {
        // The product of two floats is exact in a double; rounded toward zero to a float, as the GPU does.
        double exact = (double)v * InverseTwoPi;
        float t = (float)exact;
        if (Math.Abs((double)t) > Math.Abs(exact))
            t = t > 0f ? MathF.BitDecrement(t) : MathF.BitIncrement(t);
        return t - Math.Floor(t);
    }

    private static float GpuSin(float v) => GpuTranscendentals ? (float)Math.Sin(Turns(v) * 2.0 * Math.PI) : MathF.Sin(v);
    private static float GpuCos(float v) => GpuTranscendentals ? (float)Math.Cos(Turns(v) * 2.0 * Math.PI) : MathF.Cos(v);
    private static float GpuTan(float v) => GpuTranscendentals ? GpuSin(v) / GpuCos(v) : MathF.Tan(v);
    private static float GpuExp(float v) => GpuTranscendentals ? MathF.Pow(2f, v * 1.44269504f) : MathF.Exp(v);
    private static float GpuLog(float v) => GpuTranscendentals ? MathF.Log2(v) * 0.693147182f : MathF.Log(v);
    private static float GpuLog10(float v) => GpuTranscendentals ? MathF.Log2(v) * 0.301029996f : MathF.Log10(v);

    // -- loop attributes ---------------------------------------------------------------------------
    // C# cannot attach an attribute to a statement. A call to one of these right before a loop or
    // a branch becomes its attribute in SDSL: Unroll(); for (...) → [unroll] for (...).

    /// <summary>The next loop is unrolled: <c>[unroll]</c>.</summary>
    public static void Unroll() { }

    /// <summary>The next loop is unrolled this many times: <c>[unroll(n)]</c>.</summary>
    public static void Unroll(int count) { }

    /// <summary>The next loop is kept as a loop: <c>[loop]</c>.</summary>
    public static void Loop() { }

    /// <summary>The next if is a real branch: <c>[branch]</c>.</summary>
    public static void Branch() { }

    /// <summary>The next if is flattened, both sides evaluated: <c>[flatten]</c>.</summary>
    public static void Flatten() { }

    /// <summary>Any statement attribute, as written: <c>Attribute("fastopt");</c> before a loop is <c>[fastopt]</c>.</summary>
    public static void Attribute(string text) { }

    /// <summary>The pixel is discarded: <c>discard;</c></summary>
    public static void discard() { if (Csl.Cpu.Lane.Current is { } lane) lane.Discard(); }

    // -- synchronisation -----------------------------------------------------------------------------

    public static void GroupMemoryBarrier() { }
    public static void GroupMemoryBarrierWithGroupSync() => Csl.Cpu.Lane.Current?.GroupSync();
    public static void DeviceMemoryBarrier() { }
    public static void DeviceMemoryBarrierWithGroupSync() => Csl.Cpu.Lane.Current?.GroupSync();
    public static void AllMemoryBarrier() { }
    public static void AllMemoryBarrierWithGroupSync() => Csl.Cpu.Lane.Current?.GroupSync();

    // -- atomics -------------------------------------------------------------------------------------
    // On the GPU the destination is group-shared memory or a RW resource element. The C# versions
    // act on the reference they are given, so shader code can be unit tested on one thread.

    public static void InterlockedAdd(ref uint dest, uint value) => dest += value;
    public static void InterlockedAdd(ref uint dest, uint value, out uint original) { original = dest; dest += value; }
    public static void InterlockedAdd(ref int dest, int value) => dest += value;
    public static void InterlockedAdd(ref int dest, int value, out int original) { original = dest; dest += value; }
    public static void InterlockedAnd(ref uint dest, uint value) => dest &= value;
    public static void InterlockedAnd(ref uint dest, uint value, out uint original) { original = dest; dest &= value; }
    public static void InterlockedOr(ref uint dest, uint value) => dest |= value;
    public static void InterlockedOr(ref uint dest, uint value, out uint original) { original = dest; dest |= value; }
    public static void InterlockedXor(ref uint dest, uint value) => dest ^= value;
    public static void InterlockedXor(ref uint dest, uint value, out uint original) { original = dest; dest ^= value; }
    public static void InterlockedMin(ref uint dest, uint value) => dest = Math.Min(dest, value);
    public static void InterlockedMin(ref int dest, int value) => dest = Math.Min(dest, value);
    public static void InterlockedMax(ref uint dest, uint value) => dest = Math.Max(dest, value);
    public static void InterlockedMax(ref int dest, int value) => dest = Math.Max(dest, value);
    public static void InterlockedExchange(ref uint dest, uint value, out uint original) { original = dest; dest = value; }
    public static void InterlockedExchange(ref int dest, int value, out int original) { original = dest; dest = value; }
    public static void InterlockedCompareExchange(ref uint dest, uint compare, uint value, out uint original) { original = dest; if (dest == compare) dest = value; }
    public static void InterlockedCompareStore(ref uint dest, uint compare, uint value) { if (dest == compare) dest = value; }
    public static void InterlockedMin(ref uint dest, uint value, out uint original) { original = dest; dest = Math.Min(dest, value); }
    public static void InterlockedMin(ref int dest, int value, out int original) { original = dest; dest = Math.Min(dest, value); }
    public static void InterlockedMax(ref uint dest, uint value, out uint original) { original = dest; dest = Math.Max(dest, value); }
    public static void InterlockedMax(ref int dest, int value, out int original) { original = dest; dest = Math.Max(dest, value); }
    public static void InterlockedAnd(ref int dest, int value) => dest &= value;
    public static void InterlockedOr(ref int dest, int value) => dest |= value;
    public static void InterlockedXor(ref int dest, int value) => dest ^= value;
    public static void InterlockedCompareExchange(ref int dest, int compare, int value, out int original) { original = dest; if (dest == compare) dest = value; }

    // The destination as a value: an element of a RW resource, which C# cannot pass by reference.
    // On the CPU these do nothing to the resource: write the element and read it back instead.
    public static void InterlockedAdd(uint dest, uint value) { }
    public static void InterlockedAdd(uint dest, uint value, out uint original) => original = dest;
    public static void InterlockedAdd(int dest, int value) { }
    public static void InterlockedAdd(int dest, int value, out int original) => original = dest;
    public static void InterlockedAnd(uint dest, uint value) { }
    public static void InterlockedAnd(uint dest, uint value, out uint original) => original = dest;
    public static void InterlockedOr(uint dest, uint value) { }
    public static void InterlockedOr(uint dest, uint value, out uint original) => original = dest;
    public static void InterlockedXor(uint dest, uint value) { }
    public static void InterlockedXor(uint dest, uint value, out uint original) => original = dest;
    public static void InterlockedMin(uint dest, uint value) { }
    public static void InterlockedMin(uint dest, uint value, out uint original) => original = dest;
    public static void InterlockedMin(int dest, int value) { }
    public static void InterlockedMin(int dest, int value, out int original) => original = dest;
    public static void InterlockedMax(uint dest, uint value) { }
    public static void InterlockedMax(uint dest, uint value, out uint original) => original = dest;
    public static void InterlockedMax(int dest, int value) { }
    public static void InterlockedMax(int dest, int value, out int original) => original = dest;
    public static void InterlockedExchange(uint dest, uint value, out uint original) => original = dest;
    public static void InterlockedExchange(int dest, int value, out int original) => original = dest;
    public static void InterlockedCompareExchange(uint dest, uint compare, uint value, out uint original) => original = dest;
    public static void InterlockedCompareStore(uint dest, uint compare, uint value) { }
}
