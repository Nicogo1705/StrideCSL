using System;

namespace Csl;

/// <summary>The type of a <c>LinkType</c> generic parameter: the name of a parameter key.</summary>
[System.Diagnostics.DebuggerNonUserCode]
public readonly struct LinkType { }

/// <summary>The type of a <c>Semantic</c> generic parameter: a semantic name.</summary>
[System.Diagnostics.DebuggerNonUserCode]
public readonly struct Semantic { }

/// <summary>The type of a <c>MemberName</c> generic parameter: the name of a member, used as <c>value.TName</c>, written <c>Sdsl.Member(value, TName)</c>.</summary>
[System.Diagnostics.DebuggerNonUserCode]
public readonly struct MemberName
{
    /// <summary>A value for the parameter, for shader code run on the CPU: <c>new MemberName("rgba")</c>.</summary>
    public MemberName(string name) => Name = name;

    public string? Name { get; }
}

/// <summary>What the body of an external shader's method runs: nothing, the shader is compiled from its own source.</summary>
[System.Diagnostics.DebuggerNonUserCode]
public static class Gpu
{
    public static Exception Only => new NotSupportedException("Shader code runs on the GPU");
}

/// <summary>
/// SDSL constructs C# has no syntax for, as calls the translator turns back into them. On the CPU
/// (Csl.Cpu) they do what the SDSL does, with the macros of the run.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public static class Sdsl
{
    /// <summary>A preprocessor condition: <c>if (Sdsl.If("A")) { } else if (Sdsl.If("B")) { } else { }</c> is <c>#if A / #elif B / #else / #endif</c>.</summary>
    public static bool If(string condition) => Csl.Cpu.Run.CurrentMacros.Evaluate(condition);

    /// <summary>A macro's value in an expression: <c>Sdsl.Macro("ThreadNumberX")</c> is <c>ThreadNumberX</c>.</summary>
    public static dynamic Macro(string name) => Csl.Cpu.Run.CurrentMacros.Value(name);

    /// <summary>A macro used as a statement of its own.</summary>
    public static void MacroStatement(string name) => throw new NotSupportedException($"The macro {name} is code: it does not run on the CPU");

    /// <summary><c>value.TName</c> where TName is a MemberName generic parameter; assignable.</summary>
    public static ref dynamic Member(object value, MemberName name)
    {
        // Read on the CPU; a write through it is lost (C# has no reference to a swizzle).
        Slot.Value = Csl.Cpu.Members.Get(value, name.Name ?? throw new InvalidOperationException("The MemberName generic parameter has no value in this run (SetGeneric)"));
        return ref Slot.Value;
    }

    /// <summary><c>value.name</c> on a value C# has no members for (a stream structure, a matrix's <c>_m00_m11</c>); assignable.</summary>
    public static ref dynamic Member(object value, string name)
    {
        Slot.Value = Csl.Cpu.Members.Get(value, name);
        return ref Slot.Value;
    }

    /// <summary><c>base.M()</c> where M is not in the C# base class but in a [Mixin]: <c>Sdsl.Base(this).M()</c>.</summary>
    public static T Base<T>(T self) => self;

    /// <summary>
    /// A value passed to an inout parameter that C# cannot pass by reference (a swizzle, a stream):
    /// <c>F(ref Sdsl.Ref(v.xyz))</c> is <c>F(v.xyz)</c>.
    /// </summary>
    public static ref T Ref<T>(T value)
    {
        // On the CPU the callee writes a copy: what it writes to the swizzle or the stream is lost.
        Slot<T>.Value = value;
        return ref Slot<T>.Value;
    }

    /// <summary>
    /// An out parameter HLSL leaves undefined on some path, or reads before writing: C# wants it
    /// assigned. Nothing in the SDSL.
    /// </summary>
    public static void Undefined<T>(out T value) => value = default!;

#pragma warning disable CS8618 // thread-static: no initializer runs on the other threads
    private static class Slot
    {
        [ThreadStatic] public static dynamic Value;
    }

    private static class Slot<T>
    {
        [ThreadStatic] public static T Value;
    }
#pragma warning restore CS8618

    /// <summary>A shader's member reached through its name: <c>Sdsl.Static&lt;BlendUtils&gt;().BasicBlend(a, b)</c> is <c>BlendUtils.BasicBlend(a, b)</c>.</summary>
    public static T Static<T>() where T : class => Csl.Cpu.Mixins.Static<T>();

    /// <summary>As <see cref="Static{T}()"/>, for a generic shader: <c>Sdsl.Static&lt;S&gt;("8")</c> is <c>S&lt;8&gt;</c>.</summary>
    public static T Static<T>(string generics) where T : class => Csl.Cpu.Mixins.Static<T>();

    /// <summary>The initializer of a <c>const</c> local C# cannot declare const: <c>float3 x = Sdsl.Const(a * b);</c>.</summary>
    public static T Const<T>(T value) => value;

    /// <summary>The initializer of a <c>static const</c> local: <c>uint[] table = Sdsl.StaticConst(new uint[] { 1, 2 });</c>.</summary>
    public static T StaticConst<T>(T value) => value;

    /// <summary>An explicit conversion C# does not have (a bool to a number): <c>Sdsl.Cast&lt;float&gt;(b)</c> is <c>(float)b</c>.</summary>
    public static T Cast<T>(object value) => Csl.Cpu.HlslConvert.Convert<T>(value);

    /// <summary>
    /// A conversion HLSL makes on its own and C# wants spelled out: a float4 narrowed to a float3,
    /// an int used as a condition. Nothing in the SDSL: <c>Sdsl.Implicit&lt;float3&gt;(v)</c> is <c>v</c>.
    /// </summary>
    public static T Implicit<T>(object value) => Csl.Cpu.HlslConvert.Convert<T>(value);
}
