using System;
using System.Threading;

namespace Csl.Types;

/// <summary>
/// The intrinsics that are not plain maths: group synchronisation, atomics, and the loop attribute
/// markers. The maths is in Intrinsics.g.cs.
/// </summary>
public static partial class Intrinsics
{
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
    public static void discard() { }

    // -- synchronisation -----------------------------------------------------------------------------

    public static void GroupMemoryBarrier() { }
    public static void GroupMemoryBarrierWithGroupSync() { }
    public static void DeviceMemoryBarrier() { }
    public static void DeviceMemoryBarrierWithGroupSync() { }
    public static void AllMemoryBarrier() { }
    public static void AllMemoryBarrierWithGroupSync() { }

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
    // Shader code only; on the CPU these do nothing to the resource.
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
