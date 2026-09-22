using System;

namespace Csl;

/// <summary>The type of a <c>LinkType</c> generic parameter: the name of a parameter key.</summary>
public readonly struct LinkType { }

/// <summary>The type of a <c>Semantic</c> generic parameter: a semantic name.</summary>
public readonly struct Semantic { }

/// <summary>The type of a <c>MemberName</c> generic parameter: the name of a member, used as <c>value.TName</c>, written <c>Sdsl.Member(value, TName)</c>.</summary>
public readonly struct MemberName { }

/// <summary>What the body of an external shader's method runs: nothing, the shader is compiled from its own source.</summary>
public static class Gpu
{
    public static Exception Only => new NotSupportedException("Shader code runs on the GPU");
}

/// <summary>
/// SDSL constructs C# has no syntax for, as calls the translator turns back into them. None of these
/// runs: they are markers in shader code.
/// </summary>
public static class Sdsl
{
    /// <summary>A preprocessor condition: <c>if (Sdsl.If("A")) { } else if (Sdsl.If("B")) { } else { }</c> is <c>#if A / #elif B / #else / #endif</c>.</summary>
    public static bool If(string condition) => throw Gpu.Only;

    /// <summary>A macro's value in an expression: <c>Sdsl.Macro("ThreadNumberX")</c> is <c>ThreadNumberX</c>.</summary>
    public static dynamic Macro(string name) => throw Gpu.Only;

    /// <summary>A macro used as a statement of its own.</summary>
    public static void MacroStatement(string name) => throw Gpu.Only;

    /// <summary><c>value.TName</c> where TName is a MemberName generic parameter.</summary>
    public static dynamic Member(object value, MemberName name) => throw Gpu.Only;

    /// <summary><c>value.name</c> on a value C# has no members for (a stream structure).</summary>
    public static dynamic Member(object value, string name) => throw Gpu.Only;

    /// <summary>A shader's member reached through its name: <c>Sdsl.Static&lt;BlendUtils&gt;().BasicBlend(a, b)</c> is <c>BlendUtils.BasicBlend(a, b)</c>.</summary>
    public static T Static<T>() where T : class => throw Gpu.Only;

    /// <summary>As <see cref="Static{T}()"/>, for a generic shader: <c>Sdsl.Static&lt;S&gt;("8")</c> is <c>S&lt;8&gt;</c>.</summary>
    public static T Static<T>(string generics) where T : class => throw Gpu.Only;

    /// <summary>The initializer of a <c>const</c> local C# cannot declare const: <c>float3 x = Sdsl.Const(a * b);</c>.</summary>
    public static T Const<T>(T value) => value;

    /// <summary>The initializer of a <c>static const</c> local: <c>uint[] table = Sdsl.StaticConst(new uint[] { 1, 2 });</c>.</summary>
    public static T StaticConst<T>(T value) => value;

    /// <summary>An explicit conversion C# does not have (a bool to a number): <c>Sdsl.Cast&lt;float&gt;(b)</c> is <c>(float)b</c>.</summary>
    public static T Cast<T>(object value) => throw Gpu.Only;
}
