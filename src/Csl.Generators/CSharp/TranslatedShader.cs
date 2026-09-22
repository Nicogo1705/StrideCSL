using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Csl.Generators.CSharp;

/// <summary>The SDSL of one [Shader] class, or the reasons there is none.</summary>
public sealed class TranslatedShader
{
    public TranslatedShader(string className, string shaderName, string? ns, string path)
    {
        ClassName = className;
        ShaderName = shaderName;
        Namespace = ns;
        Path = path;
    }

    /// <summary>The C# class, as declared.</summary>
    public string ClassName { get; }

    /// <summary>The SDSL shader name: [Shader(Name = ...)] or the class name.</summary>
    public string ShaderName { get; }

    public string? Namespace { get; }

    /// <summary>The C# file of the first declaration, for messages.</summary>
    public string Path { get; }

    /// <summary>[Shader(External = true)]: a description of a shader that exists elsewhere; no SDSL.</summary>
    public bool IsExternal { get; set; }

    public string? Sdsl { get; set; }

    /// <summary>C# stubs of the members of the [Mixin] shaders, so the class compiles against them; null when there are none.</summary>
    public string? MixinStubs { get; set; }

    /// <summary>[NumThreads] on the class, or null.</summary>
    public (int X, int Y, int Z)? NumThreads { get; set; }

    public List<Diagnostic> Diagnostics { get; } = new List<Diagnostic>();

    public bool HasErrors
    {
        get
        {
            foreach (var d in Diagnostics)
                if (d.Severity == DiagnosticSeverity.Error)
                    return true;
            return false;
        }
    }
}
