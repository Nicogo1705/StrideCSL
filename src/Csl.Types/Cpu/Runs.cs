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
/// <see cref="CpuTexture"/>: PSMain for each pixel, TexCoord at the pixel's centre, in 2x2 quads that
/// run in lockstep for ddx, ddy and Sample's level, as a full-viewport draw on the GPU.
/// </summary>
public sealed class CpuImageEffect : Run
{
    public CpuImageEffect(Type shader, int width, int height) : base(shader, "PSMain")
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The render target's format: what the colour is rounded to when written.</summary>
    public TexelFormat Format { get; set; } = TexelFormat.Rgba8UNorm;

    /// <summary>The streams a pixel starts with; by default what SpriteBase's vertex shader gives a full-viewport quad.</summary>
    public Action<object, int, int>? PixelInputs { get; set; }

    /// <summary>The whole image.</summary>
    public CpuTexture Draw()
    {
        var target = new CpuTexture(Width, Height, format: Format);
        int quadsX = (Width + 1) / 2, quadsY = (Height + 1) / 2;
        var teams = new ConcurrentBag<LaneTeam>();
        try
        {
            Parallel.For(0, quadsX * quadsY, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, quad =>
            {
                if (!teams.TryTake(out var team))
                    team = new LaneTeam(4, Macros);
                try
                {
                    DrawQuad(team, target, quad % quadsX * 2, quad / quadsX * 2, -1, -1);
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
        return target;
    }

    /// <summary>One pixel, with its quad; with <see cref="Run.Break"/>, the debugger stops right before its lane runs.</summary>
    public float4 DrawPixel(int x, int y)
    {
        var target = new CpuTexture(Width, Height, format: Format);
        using var team = new LaneTeam(4, Macros);
        DrawQuad(team, target, x & ~1, y & ~1, x, y);
        return target.Read(0, x, y);
    }

    private void DrawQuad(LaneTeam team, CpuTexture target, int left, int top, int breakX, int breakY)
    {
        var shaders = new object[4];
        team.Run(lane =>
        {
            var shader = shaders[lane.Index];
            int x = lane.Pixel.x, y = lane.Pixel.y;
            Invoke(shader, Break && x == breakX && y == breakY);
            if (!lane.Discarded && !lane.IsHelper && Members.InstanceFields(shader.GetType()).TryGetValue("ColorTarget", out var output))
                target.Write(0, x, y, 0, (float4)output.GetValue(shader)!);
        }, lane =>
        {
            int x = left + (lane.Index & 1), y = top + (lane.Index >> 1);
            lane.Pixel = new int2(x, y);
            // Outside the target (an odd size): a helper, run for its neighbours' derivatives only.
            lane.IsHelper = x >= Width || y >= Height;
            var shader = ShaderInstances.Clone(Shader);
            if (PixelInputs != null)
                PixelInputs(shader, x, y);
            else
            {
                TrySet(shader, "TexCoord", new float2((x + 0.5f) / Width, (y + 0.5f) / Height));
                TrySet(shader, "ShadingPosition", new float4(x + 0.5f, y + 0.5f, 0f, 1f));
            }
            shaders[lane.Index] = shader;
        });
    }
}

/// <summary>
/// A compute shader dispatched on the CPU: each group's threads run in lockstep at
/// GroupMemoryBarrierWithGroupSync, groups one after the other (group-shared statics are the group's).
/// </summary>
public sealed class CpuComputeShader : Run
{
    public CpuComputeShader(Type shader) : base(shader, "CSMain")
    {
        var numThreads = Enumerable.Repeat(shader, 1).Concat(BaseTypes(shader)).Select(t => t.GetCustomAttribute<NumThreadsAttribute>(inherit: false)).FirstOrDefault(a => a != null)
            ?? throw new InvalidOperationException(shader.Name + " has no [NumThreads]");
        ThreadsX = numThreads.X;
        ThreadsY = numThreads.Y;
        ThreadsZ = numThreads.Z;
    }

    public int ThreadsX { get; }
    public int ThreadsY { get; }
    public int ThreadsZ { get; }

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
        int size = ThreadsX * ThreadsY * ThreadsZ;
        using var team = new LaneTeam(size, Macros);
        var shaders = new object[size];
        for (int gz = 0; gz < groupsZ; gz++)
        for (int gy = 0; gy < groupsY; gy++)
        for (int gx = 0; gx < groupsX; gx++)
        {
            var group = new uint3((uint)gx, (uint)gy, (uint)gz);
            ResetGroupShared();
            team.Run(lane =>
            {
                var shader = shaders[lane.Index];
                var id = (uint3)Members.Get(shader, "DispatchThreadId");
                Invoke(shader, Break && breakAt is { } at && at.x == id.x && at.y == id.y && at.z == id.z);
            }, lane =>
            {
                int i = lane.Index;
                var thread = new uint3((uint)(i % ThreadsX), (uint)(i / ThreadsX % ThreadsY), (uint)(i / (ThreadsX * ThreadsY)));
                var shader = ShaderInstances.Clone(Shader);
                TrySet(shader, "GroupId", group);
                TrySet(shader, "GroupThreadId", thread);
                TrySet(shader, "DispatchThreadId", new uint3(group.x * (uint)ThreadsX + thread.x, group.y * (uint)ThreadsY + thread.y, group.z * (uint)ThreadsZ + thread.z));
                TrySet(shader, "GroupIndex", (uint)i);
                shaders[i] = shader;
            });
        }
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
