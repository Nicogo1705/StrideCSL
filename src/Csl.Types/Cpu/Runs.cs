using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Csl.Types;

namespace Csl.Cpu;

/// <summary>
/// A C# shader run on the CPU: the shader's own code (and the engine's, from Csl.Engine), one lane per
/// pixel or per thread, as the GPU would run it. Configure it as the effect would be (<see cref="Set"/>
/// the parameters and resources, <see cref="Macros"/>), then run it; <see cref="Break"/> stops in the
/// debugger right before the lane you want, so its C# can be stepped through.
/// </summary>
public abstract class Run
{
    [ThreadStatic] private static object? currentShader;

    /// <summary>The macros of this thread's lane; the Direct3D 11 defaults outside a run.</summary>
    public static Macros CurrentMacros => Lane.Current?.Macros ?? DefaultMacros;

    private static readonly Macros DefaultMacros = Macros.Direct3D11();

    /// <summary>The shader object of this thread's lane, null outside a run.</summary>
    public static object? CurrentShader => currentShader;

    protected Run(Type shader, string entryPoint)
    {
        ShaderType = shader;
        Shader = ShaderInstances.Create(shader);
        var method = shader.GetMethod(entryPoint, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(shader.Name, entryPoint);
        var parameter = Expression.Parameter(typeof(object));
        Entry = Expression.Lambda<Action<object>>(Expression.Call(Expression.Convert(parameter, method.DeclaringType!), method), parameter).Compile();
    }

    public Type ShaderType { get; }

    /// <summary>The configured shader, which each lane gets a copy of.</summary>
    public object Shader { get; }

    public Macros Macros { get; set; } = Macros.Direct3D11();

    protected Action<object> Entry { get; }

    /// <summary>Calls Debugger.Break right before this lane's entry point, when a debugger is attached.</summary>
    public bool Break { get; set; }

    /// <summary>A parameter, a resource or a stream of the shader (or of a mixin: the same name), by name.</summary>
    public Run Set(string name, object value)
    {
        Members.Set(Shader, name, value);
        return this;
    }

    /// <summary>A generic parameter of the shader (a [Generic] static field): <c>SetGeneric("TRgba", new MemberName("rgba"))</c>.</summary>
    public Run SetGeneric(string name, object value)
    {
        for (var type = ShaderType; type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                field.SetValue(null, value);
                return this;
            }
        }
        throw new MissingFieldException(ShaderType.Name, name);
    }

    protected void Invoke(object lane, bool breakHere)
    {
        currentShader = lane;
        try
        {
            if (breakHere && Debugger.IsAttached)
                Debugger.Break(); // Step into (F11) the next line: the shader's entry point for the lane asked for.
            Entry(lane);
        }
        finally
        {
            currentShader = null;
        }
    }

    protected static void TrySet(object shader, string name, object value)
    {
        if (Members.InstanceFields(shader.GetType()).TryGetValue(name, out var field) && field.FieldType == value.GetType())
            field.SetValue(shader, value);
    }
}

/// <summary>
/// An image effect (a shader deriving from the engine's ImageEffectShader) drawn on the CPU into a
/// <see cref="CpuTexture"/>: PSMain for each pixel, TexCoord at the pixel's centre, as a draw over the
/// viewport on the GPU. Each 2x2 quad first runs straight on one thread; a derivative (ddx, ddy, the
/// level of a Sample that needs one) runs it again with its four lanes in lockstep, and every quad
/// after it too.
/// </summary>
public sealed class CpuImageEffect : Run
{
    private readonly Action<object, float2>? setTexCoord;
    private readonly Action<object, float4>? setPosition;
    private readonly Func<object, float4>? getColor;
    private volatile bool lockstep;

    public CpuImageEffect(Type shader, int width, int height) : base(shader, "PSMain")
    {
        Width = width;
        Height = height;
        var type = Shader.GetType();
        setTexCoord = Members.Setter<float2>(type, "TexCoord");
        setPosition = Members.Setter<float4>(type, "ShadingPosition");
        getColor = Members.Getter<float4>(type, "ColorTarget");
        directQuad = new System.Threading.ThreadLocal<(Lane[], object[])>(() =>
        {
            var lanes = new Lane[4];
            var shaders = new object[4];
            for (int i = 0; i < 4; i++)
            {
                lanes[i] = new Lane(i, null, Macros, isPixel: true);
                shaders[i] = ShaderInstances.Clone(Shader);
            }
            return (lanes, shaders);
        });
    }

    private readonly System.Threading.ThreadLocal<(Lane[] Lanes, object[] Shaders)> directQuad;

    public int Width { get; }
    public int Height { get; }

    /// <summary>The render target's format: what the colour is rounded to when written.</summary>
    public TexelFormat Format { get; set; } = TexelFormat.Rgba8UNorm;

    /// <summary>Where the pixels of this run are in the window: SV_Position is offset by it.</summary>
    public int Left { get; set; }
    public int Top { get; set; }

    /// <summary>The viewport TexCoord goes across, in window pixels; by default the run's own pixels.</summary>
    public (float X, float Y, float Width, float Height)? Viewport { get; set; }

    /// <summary>The streams a pixel starts with, instead of what SpriteBase's vertex shader gives a quad over the viewport.</summary>
    public Action<object, int, int>? PixelInputs { get; set; }

    /// <summary>Whether the shader needed its quads in lockstep: it takes derivatives.</summary>
    public bool NeedsQuads => lockstep;

    /// <summary>The whole image.</summary>
    public CpuTexture Draw()
    {
        var target = new CpuTexture(Width, Height, format: Format);
        int quadsX = (Width + 1) / 2, quadsY = (Height + 1) / 2;
        var teams = new ConcurrentBag<LaneTeam>();
        try
        {
            Parallel.For(0, quadsY, row =>
            {
                LaneTeam? team = null;
                try
                {
                    for (int column = 0; column < quadsX; column++)
                    {
                        if (!lockstep && DirectQuad(target, column * 2, row * 2))
                            continue;
                        lockstep = true;
                        if (team == null && !teams.TryTake(out team))
                            team = new LaneTeam(4, Macros, isPixel: true);
                        TeamQuad(team, target, column * 2, row * 2, -1, -1);
                    }
                }
                finally
                {
                    if (team != null)
                        teams.Add(team);
                }
            });
        }
        finally
        {
            foreach (var team in teams)
                team.Dispose();
        }
        return target;
    }

    /// <summary>One pixel, with its quad in lockstep; with <see cref="Run.Break"/>, the debugger stops right before its lane runs.</summary>
    public float4 DrawPixel(int x, int y)
    {
        var target = new CpuTexture(Width, Height, format: Format);
        using var team = new LaneTeam(4, Macros, isPixel: true);
        TeamQuad(team, target, x & ~1, y & ~1, x, y);
        return target.Read(0, x, y);
    }

    /// <summary>The quad's four lanes one after the other on this thread; false when one needs the others.</summary>
    private bool DirectQuad(CpuTexture target, int left, int top)
    {
        // The thread's own four lanes and shader objects, reset from the configured shader for each
        // quad: no allocation per pixel.
        var (lanes, shaders) = directQuad.Value!;
        for (int i = 0; i < 4; i++)
        {
            lanes[i].Discarded = false;
            shaders[i] = Prepare(lanes[i], left, top, shaders[i]);
        }
        for (int i = 0; i < 4; i++)
        {
            Lane.Enter(lanes[i]);
            try
            {
                Invoke(shaders[i], false);
            }
            catch (NeedsLockstep)
            {
                return false;
            }
            finally
            {
                Lane.Leave();
            }
        }
        for (int i = 0; i < 4; i++)
            Finish(lanes[i], shaders[i], target);
        return true;
    }

    private void TeamQuad(LaneTeam team, CpuTexture target, int left, int top, int breakX, int breakY)
    {
        var shaders = new object[4];
        team.Run(lane =>
        {
            Invoke(shaders[lane.Index], Break && lane.Pixel.x == breakX && lane.Pixel.y == breakY);
            Finish(lane, shaders[lane.Index], target);
        }, lane => shaders[lane.Index] = Prepare(lane, left, top));
    }

    private object Prepare(Lane lane, int left, int top, object? reuse = null)
    {
        int x = left + (lane.Index & 1), y = top + (lane.Index >> 1);
        lane.Pixel = new int2(x, y);
        // Outside the target (an odd size): a helper, run for its neighbours' derivatives only.
        lane.IsHelper = x >= Width || y >= Height;
        object shader;
        if (reuse != null)
        {
            Members.Copy(Shader, reuse);
            shader = reuse;
        }
        else
        {
            shader = ShaderInstances.Clone(Shader);
        }
        if (PixelInputs != null)
        {
            PixelInputs(shader, x, y);
            return shader;
        }
        var (vx, vy, vw, vh) = Viewport ?? (Left, Top, Width, Height);
        float px = Left + x + 0.5f, py = Top + y + 0.5f;
        setTexCoord?.Invoke(shader, new float2((px - vx) / vw, (py - vy) / vh));
        setPosition?.Invoke(shader, new float4(px, py, 0f, 1f));
        return shader;
    }

    private void Finish(Lane lane, object shader, CpuTexture target)
    {
        if (!lane.Discarded && !lane.IsHelper && getColor != null)
            target.Write(0, lane.Pixel.x, lane.Pixel.y, 0, getColor(shader));
    }
}

/// <summary>
/// A compute shader dispatched on the CPU. Each group's threads first run straight, one after the
/// other; GroupMemoryBarrierWithGroupSync runs the group again with its threads in lockstep, and every
/// group after it. Groups run in parallel, unless the shader has [GroupShared] statics: then one after
/// the other, the statics being the group's memory.
/// </summary>
public sealed class CpuComputeShader : Run
{
    private readonly Action<object, uint3>? setGroupId, setGroupThreadId, setDispatchThreadId;
    private readonly Action<object, uint>? setGroupIndex;
    private volatile bool lockstep;

    private readonly bool engineMain;
    private readonly Action<object, int>? setThreadCountX, setThreadCountY, setThreadCountZ;
    private readonly Action<object, uint>? setThreadCountPerGroup, setThreadGroupIndex;
    private readonly Action<object, uint3>? setThreadGroupCount;
    private int3 groupCount;

    /// <summary>
    /// The engine's CSMain only fills streams from the macros and the group, then calls Compute: when the
    /// shader keeps it, the run fills them itself and calls Compute, sparing each thread six dynamic
    /// macro reads. A shader that overrides CSMain has it run.
    /// </summary>
    private static string EntryOf(Type shader)
        => shader.GetMethod("CSMain", Type.EmptyTypes)?.DeclaringType?.FullName == "Csl.Engine.ComputeShaderBase" && shader.GetMethod("Compute", Type.EmptyTypes) != null ? "Compute" : "CSMain";

    public CpuComputeShader(Type shader) : base(shader, EntryOf(shader))
    {
        var numThreads = Enumerable.Repeat(shader, 1).Concat(BaseTypes(shader)).Select(t => t.GetCustomAttribute<NumThreadsAttribute>(inherit: false)).FirstOrDefault(a => a != null)
            ?? throw new InvalidOperationException(shader.Name + " has no [NumThreads]");
        ThreadsX = numThreads.X;
        ThreadsY = numThreads.Y;
        ThreadsZ = numThreads.Z;
        var type = Shader.GetType();
        setGroupId = Members.Setter<uint3>(type, "GroupId");
        setGroupThreadId = Members.Setter<uint3>(type, "GroupThreadId");
        setDispatchThreadId = Members.Setter<uint3>(type, "DispatchThreadId");
        setGroupIndex = Members.Setter<uint>(type, "GroupIndex");
        engineMain = EntryOf(shader) == "Compute";
        setThreadCountX = Members.Setter<int>(type, "ThreadCountX");
        setThreadCountY = Members.Setter<int>(type, "ThreadCountY");
        setThreadCountZ = Members.Setter<int>(type, "ThreadCountZ");
        setThreadCountPerGroup = Members.Setter<uint>(type, "ThreadCountPerGroup");
        setThreadGroupIndex = Members.Setter<uint>(type, "ThreadGroupIndex");
        setThreadGroupCount = Members.Setter<uint3>(type, "ThreadGroupCount");
        directThread = new System.Threading.ThreadLocal<object>(() => ShaderInstances.Clone(Shader));
    }

    public int ThreadsX { get; }
    public int ThreadsY { get; }
    public int ThreadsZ { get; }

    /// <summary>Whether the shader needed its groups in lockstep: it has a barrier.</summary>
    public bool NeedsGroups => lockstep;

    private static System.Collections.Generic.IEnumerable<Type> BaseTypes(Type type)
    {
        for (var current = type.BaseType; current != null && current != typeof(object); current = current.BaseType)
            yield return current;
    }

    /// <summary>Runs every group; <paramref name="breakAt"/> (a DispatchThreadId) stops in the debugger before that thread.</summary>
    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1, uint3? breakAt = null)
    {
        Macros.Set("ThreadNumberX", ThreadsX).Set("ThreadNumberY", ThreadsY).Set("ThreadNumberZ", ThreadsZ);
        TrySet(Shader, "ThreadGroupCountGlobal", new int3(groupsX, groupsY, groupsZ));
        groupCount = new int3(groupsX, groupsY, groupsZ);
        int size = ThreadsX * ThreadsY * ThreadsZ;
        int count = groupsX * groupsY * groupsZ;
        uint3 GroupOf(int g) => new uint3((uint)(g % groupsX), (uint)(g / groupsX % groupsY), (uint)(g / (groupsX * groupsY)));
        bool breaking = Break && breakAt != null;

        if (HasGroupShared() || breaking)
        {
            // One after the other: the [GroupShared] statics are one group's at a time; a break, one thread's.
            LaneTeam? team = null;
            try
            {
                for (int g = 0; g < count; g++)
                {
                    ResetGroupShared();
                    if (!lockstep && !breaking && DirectGroup(GroupOf(g), size))
                        continue;
                    if (!breaking)
                        lockstep = true;
                    team ??= new LaneTeam(size, Macros, isPixel: false);
                    TeamGroup(team, GroupOf(g), size, breakAt);
                }
            }
            finally
            {
                team?.Dispose();
            }
            return;
        }

        var teams = new ConcurrentBag<LaneTeam>();
        try
        {
            Parallel.For(0, count, g =>
            {
                if (!lockstep && DirectGroup(GroupOf(g), size))
                    return;
                lockstep = true;
                if (!teams.TryTake(out var team))
                    team = new LaneTeam(size, Macros, isPixel: false);
                try
                {
                    TeamGroup(team, GroupOf(g), size, null);
                }
                finally
                {
                    teams.Add(team);
                }
            });
        }
        finally
        {
            foreach (var team in teams)
                team.Dispose();
        }
    }

    /// <summary>The group's threads one after the other on this thread; false when one reaches a barrier.</summary>
    private bool DirectGroup(uint3 group, int size)
    {
        var lane = new Lane(0, null, Macros, isPixel: false);
        var reuse = directThread.Value!;
        for (int i = 0; i < size; i++)
        {
            var shader = Prepare(i, group, reuse);
            Lane.Enter(lane);
            try
            {
                Invoke(shader, false);
            }
            catch (NeedsLockstep)
            {
                return false;
            }
            finally
            {
                Lane.Leave();
            }
        }
        return true;
    }

    private void TeamGroup(LaneTeam team, uint3 group, int size, uint3? breakAt)
    {
        var shaders = new object[size];
        var ids = new uint3[size];
        team.Run(lane =>
        {
            var id = ids[lane.Index];
            Invoke(shaders[lane.Index], Break && breakAt is { } at && at.x == id.x && at.y == id.y && at.z == id.z);
        }, lane =>
        {
            shaders[lane.Index] = Prepare(lane.Index, group);
            ids[lane.Index] = DispatchId(lane.Index, group);
        });
    }

    private uint3 ThreadOf(int i) => new uint3((uint)(i % ThreadsX), (uint)(i / ThreadsX % ThreadsY), (uint)(i / (ThreadsX * ThreadsY)));

    private uint3 DispatchId(int i, uint3 group)
    {
        var thread = ThreadOf(i);
        return new uint3(group.x * (uint)ThreadsX + thread.x, group.y * (uint)ThreadsY + thread.y, group.z * (uint)ThreadsZ + thread.z);
    }

    private readonly System.Threading.ThreadLocal<object> directThread;

    private object Prepare(int i, uint3 group, object? reuse = null)
    {
        object shader;
        if (reuse != null)
        {
            Members.Copy(Shader, reuse);
            shader = reuse;
        }
        else
        {
            shader = ShaderInstances.Clone(Shader);
        }
        setGroupId?.Invoke(shader, group);
        setGroupThreadId?.Invoke(shader, ThreadOf(i));
        setDispatchThreadId?.Invoke(shader, DispatchId(i, group));
        setGroupIndex?.Invoke(shader, (uint)i);
        if (engineMain)
        {
            // What ComputeShaderBase.CSMain writes before calling Compute.
            setThreadCountX?.Invoke(shader, ThreadsX);
            setThreadCountY?.Invoke(shader, ThreadsY);
            setThreadCountZ?.Invoke(shader, ThreadsZ);
            setThreadCountPerGroup?.Invoke(shader, (uint)(ThreadsX * ThreadsY * ThreadsZ));
            var count = new uint3((uint)groupCount.x, (uint)groupCount.y, (uint)groupCount.z);
            setThreadGroupCount?.Invoke(shader, count);
            setThreadGroupIndex?.Invoke(shader, (group.z * count.y + group.y) * count.x + group.x);
        }
        return shader;
    }

    private bool HasGroupShared()
    {
        for (var type = ShaderType; type != null && type != typeof(object); type = type.BaseType)
            if (type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Any(f => f.GetCustomAttribute<GroupSharedAttribute>() != null))
                return true;
        return false;
    }

    /// <summary>[GroupShared] static arrays, new for each group: group-shared memory starts undefined, here zero.</summary>
    private void ResetGroupShared()
    {
        for (var type = ShaderType; type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.GetCustomAttribute<GroupSharedAttribute>() == null)
                    continue;
                if (field.FieldType.IsArray)
                {
                    var size = field.GetCustomAttribute<SizeAttribute>()?.Sizes.FirstOrDefault();
                    int length = size != null && int.TryParse(size, out var n) ? n : Macros.Value(size ?? "") is MacroValue m ? (int)m : 0;
                    field.SetValue(null, Array.CreateInstance(field.FieldType.GetElementType()!, length));
                }
                else
                {
                    field.SetValue(null, field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null);
                }
            }
        }
    }
}
