using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Buffer = Stride.Graphics.Buffer;

namespace Csl;

/// <summary>
/// A compute shader ready to dispatch: owns the engine's ComputeEffectShader, knows its thread group
/// size, and turns a work size into thread group counts. The generated wrappers derive from it and add
/// one typed property per shader parameter.
/// </summary>
public abstract class ComputeEffect : IDisposable
{
    private readonly ComputeEffectShader shader;
    private bool disposed;

    protected ComputeEffect(IServiceRegistry services, string shaderName, Int3 threadNumbers)
    {
        if (string.IsNullOrEmpty(shaderName))
            throw new ArgumentException("A shader name is required", nameof(shaderName));
        if (threadNumbers.X < 1 || threadNumbers.Y < 1 || threadNumbers.Z < 1)
            throw new ArgumentOutOfRangeException(nameof(threadNumbers), threadNumbers, "Every axis needs at least one thread");
        Context = ShaderContext.Get(services);
        Name = shaderName;
        shader = new ComputeEffectShader(Context.RenderContext)
        {
            ShaderSourceName = shaderName,
            ThreadNumbers = threadNumbers,
            Name = shaderName,
        };
    }

    /// <summary>The SDSL shader class this dispatches.</summary>
    public string Name { get; }

    public ShaderContext Context { get; }

    /// <summary>Threads per group on each axis. Changing it recompiles the effect on the next dispatch.</summary>
    public Int3 ThreadNumbers
    {
        get => shader.ThreadNumbers;
        set => shader.ThreadNumbers = value;
    }

    /// <summary>The raw parameters, for anything the wrapper does not expose.</summary>
    public ParameterCollection Parameters => shader.Parameters;

    /// <summary>The engine object underneath, for the unusual case.</summary>
    public ComputeEffectShader Shader => shader;

    /// <summary>Groups needed to cover this many cells with these threads per group, rounded up.</summary>
    public static Int3 GroupCount(Int3 cells, Int3 threads) => new(
        (cells.X + threads.X - 1) / threads.X,
        (cells.Y + threads.Y - 1) / threads.Y,
        (cells.Z + threads.Z - 1) / threads.Z);

    /// <summary>Runs the shader over this many cells: enough groups to cover them, the shader clips the rest.</summary>
    public void Dispatch(Int3 cells) => DispatchGroups(GroupCount(cells, ThreadNumbers));

    public void Dispatch(int cellsX, int cellsY = 1, int cellsZ = 1) => Dispatch(new Int3(cellsX, cellsY, cellsZ));

    /// <summary>Runs the shader with exactly this many thread groups.</summary>
    public void DispatchGroups(Int3 groups)
    {
        if (disposed)
            throw new ObjectDisposedException(Name);
        if (groups.X < 1 || groups.Y < 1 || groups.Z < 1)
            return;
        shader.ThreadGroupCounts = groups;
        shader.Draw(Context.DrawContext);
    }

    protected void SetTexture(ref Texture? field, ObjectParameterKey<Texture> key, Texture? value, in ResourceSlot slot)
    {
        if (value != null)
        {
            var missing = slot.TextureFlags & ~value.Flags;
            if (missing != TextureFlags.None)
                throw new ArgumentException($"{slot} needs a texture with {missing}; this one was created with {value.Flags}", slot.Name);
            if (slot.NeedsTypedUavLoad && !ResourceSlot.IsTypedUavLoadFormat(value.ViewFormat))
                throw new ArgumentException($"{slot} is read and written through its RW view, which needs R32_Float, R32_UInt or R32_SInt, not {value.ViewFormat}", slot.Name);
        }
        field = value;
        Parameters.Set(key, value!);
    }

    protected void SetBuffer(ref Buffer? field, ObjectParameterKey<Buffer> key, Buffer? value, in ResourceSlot slot)
    {
        if (value != null)
        {
            var missing = slot.BufferFlags & ~value.Flags;
            if (missing != BufferFlags.None)
                throw new ArgumentException($"{slot} needs a buffer with {missing}; this one was created with {value.Flags}", slot.Name);
            if (slot.NeedsTypedUavLoad && !ResourceSlot.IsTypedUavLoadFormat(value.ViewFormat))
                throw new ArgumentException($"{slot} is read and written through its RW view, which needs R32_Float, R32_UInt or R32_SInt, not {value.ViewFormat}", slot.Name);
        }
        field = value;
        Parameters.Set(key, value!);
    }

    protected void SetSampler(ref SamplerState? field, ObjectParameterKey<SamplerState> key, SamplerState? value)
    {
        field = value;
        Parameters.Set(key, value!);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        shader.Dispose();
    }

    public override string ToString() => $"{Name} [{ThreadNumbers.X}x{ThreadNumbers.Y}x{ThreadNumbers.Z}]";
}
