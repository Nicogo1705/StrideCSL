using System;
using System.Collections.Generic;
using System.Threading;
using Csl.Types;

namespace Csl.Cpu;

/// <summary>
/// The invocation a thread runs: which pixel or which thread of a group, with the lanes it shares
/// derivatives or group memory with. Shader code reaches it through the intrinsics (ddx, discard,
/// GroupMemoryBarrierWithGroupSync), Sample (its level from the quad) and the Sdsl markers (the macros).
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class Lane
{
    [ThreadStatic] private static Lane? current;

    /// <summary>The lane of this thread, null outside a run.</summary>
    public static Lane? Current => current;

    internal Lane(int index, LaneTeam? team, Macros macros, bool isPixel)
    {
        Index = index;
        Team = team;
        Macros = macros;
        IsPixel = isPixel;
    }

    /// <summary>0 to 3 in a quad (x then y), the flattened thread index in a group.</summary>
    public int Index { get; }

    /// <summary>
    /// The threads it runs in lockstep with; null when it runs straight on the caller's thread, which
    /// is enough until a derivative or a barrier needs the others (<see cref="NeedsLockstep"/>).
    /// </summary>
    public LaneTeam? Team { get; }

    /// <summary>A pixel shader's lane, in a 2x2 quad; otherwise a compute thread.</summary>
    public bool IsPixel { get; }

    public Macros Macros { get; }

    /// <summary>The pixel a pixel shader lane shades.</summary>
    public int2 Pixel { get; internal set; }

    /// <summary>Discarded, or outside the target: its derivatives still count, its output does not.</summary>
    public bool Discarded { get; internal set; }

    public bool IsHelper { get; internal set; }

    /// <summary>discard: the pixel is not written; the lane goes on for its neighbours' derivatives, as a helper does.</summary>
    public void Discard() => Discarded = true;

    internal static void Enter(Lane lane) => current = lane;

    internal static void Leave() => current = null;

    // -- quad ----------------------------------------------------------------------------------------

    /// <summary>
    /// The value every lane of the quad passes at this point, exchanged: the four lanes wait for each
    /// other, as a quad executes in lockstep on the GPU.
    /// </summary>
    private float4[] Exchange(float4 value)
    {
        if (!IsPixel)
            throw new InvalidOperationException("Derivatives need the 2x2 quad of a pixel shader; this lane is a compute thread.");
        if (Team == null)
            throw new NeedsLockstep();
        return Team.Exchange(Index, value);
    }

    /// <summary>ddx_fine and ddy_fine of a value: the difference with the neighbour on the same row / column.</summary>
    public (float4 Ddx, float4 Ddy) DerivativesFine(float4 value)
    {
        var all = Exchange(value);
        int row = Index & 2, column = Index & 1;
        return (all[row | 1] - all[row], all[2 | column] - all[column]);
    }

    /// <summary>ddx_coarse and ddy_coarse: one value for the quad, from its top row and its left column.</summary>
    public (float4 Ddx, float4 Ddy) DerivativesCoarse(float4 value)
    {
        var all = Exchange(value);
        return (all[1] - all[0], all[2] - all[0]);
    }

    /// <summary>What ddx, ddy and the implicit level of Sample use: coarse, as fxc compiles them (deriv_rtx_coarse).</summary>
    public static (float4 Ddx, float4 Ddy) Derivatives(float4 value)
    {
        var lane = current ?? throw new InvalidOperationException("Derivatives are only defined in a pixel shader run (CpuImageEffect).");
        return Coarse ? lane.DerivativesCoarse(value) : lane.DerivativesFine(value);
    }

    /// <summary>Plain ddx/ddy and Sample's level: coarse (true, the default) or fine.</summary>
    public static bool Coarse = true;

    // -- group ---------------------------------------------------------------------------------------

    /// <summary>GroupMemoryBarrierWithGroupSync: every thread of the group reaches it before any goes on.</summary>
    public void GroupSync()
    {
        if (Team == null)
            throw new NeedsLockstep();
        Team.Sync();
    }
}

/// <summary>
/// Thrown in a lane run straight on its thread when it needs its neighbours (a derivative, a barrier):
/// the run starts that quad or group again with a <see cref="LaneTeam"/>, and keeps one from then on.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class NeedsLockstep : Exception
{
    public NeedsLockstep() : base("This lane needs its quad or its group in lockstep") { }
}

/// <summary>
/// Threads that run lanes in lockstep: 4 for a quad, a group's worth for a compute shader. Each lane
/// is a thread of its own so that one can wait for the others at a derivative or a barrier, as the GPU
/// does; a lane that returns early leaves the team, the others go on without it.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class LaneTeam : IDisposable
{
    private readonly Thread[] threads;
    private readonly Barrier barrier;
    private readonly float4[] slots;
    private readonly SemaphoreSlim[] start;
    private readonly CountdownEvent done;
    private Action<Lane>? work;
    private readonly Lane[] lanes;
    private readonly List<Exception> failures = new List<Exception>();
    private volatile bool stopping;

    public LaneTeam(int size, Macros macros, bool isPixel)
    {
        Size = size;
        slots = new float4[size];
        barrier = new Barrier(size);
        start = new SemaphoreSlim[size];
        done = new CountdownEvent(size);
        lanes = new Lane[size];
        threads = new Thread[size];
        for (int i = 0; i < size; i++)
        {
            int index = i;
            lanes[i] = new Lane(i, this, macros, isPixel);
            start[i] = new SemaphoreSlim(0);
            threads[i] = new Thread(() => Loop(index), 4 << 20) { IsBackground = true, Name = $"Csl lane {i}" };
            threads[i].Start();
        }
    }

    public int Size { get; }

    public IReadOnlyList<Lane> Lanes => lanes;

    /// <summary>Runs <paramref name="body"/> on every lane at once and waits for all of them.</summary>
    public void Run(Action<Lane> body, Action<Lane>? prepare = null)
    {
        work = body;
        failures.Clear();
        // Everyone who left last time joins again.
        int missing = Size - barrier.ParticipantCount;
        if (missing > 0)
            barrier.AddParticipants(missing);
        done.Reset(Size);
        foreach (var lane in lanes)
        {
            lane.Discarded = false;
            lane.IsHelper = false;
            prepare?.Invoke(lane);
        }
        foreach (var s in start)
            s.Release();
        done.Wait();
        work = null;
        if (failures.Count > 0)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private void Loop(int index)
    {
        while (true)
        {
            start[index].Wait();
            if (stopping)
                return;
            var lane = lanes[index];
            Lane.Enter(lane);
            try
            {
                work!(lane);
            }
            catch (Exception e)
            {
                lock (failures)
                    failures.Add(e);
            }
            finally
            {
                Lane.Leave();
                // Returned (or failed): the others no longer wait for this lane.
                barrier.RemoveParticipant();
                done.Signal();
            }
        }
    }

    internal float4[] Exchange(int index, float4 value)
    {
        slots[index] = value;
        barrier.SignalAndWait();
        var result = (float4[])slots.Clone();
        barrier.SignalAndWait();
        return result;
    }

    internal void Sync() => barrier.SignalAndWait();

    public void Dispose()
    {
        stopping = true;
        foreach (var s in start)
            s.Release();
        foreach (var t in threads)
            t.Join();
        barrier.Dispose();
        done.Dispose();
        foreach (var s in start)
            s.Dispose();
    }
}
