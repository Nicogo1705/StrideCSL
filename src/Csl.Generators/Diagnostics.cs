using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Csl.Generators;

/// <summary>Every diagnostic the generator and the analyzer can raise. CSL0xx: wrappers. CSL1xx: C# to SDSL.</summary>
public static class Diagnostics
{
    private const string Category = "Csl";

    public static readonly DiagnosticDescriptor SdslParseProblem = new DiagnosticDescriptor(
        "CSL001", "SDSL declaration not understood",
        "{0}: the wrapper for this file may be incomplete",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnmappedMemberType = new DiagnosticDescriptor(
        "CSL002", "Shader parameter type has no C# mapping",
        "Parameter '{0}' of shader '{1}' has type '{2}', which has no C# key type; set it through Parameters instead",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ArrayParameterNotWrapped = new DiagnosticDescriptor(
        "CSL003", "Array shader parameter not wrapped",
        "Parameter '{0}' of shader '{1}' is an array; set it through Parameters",
        Category, DiagnosticSeverity.Info, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateShader = new DiagnosticDescriptor(
        "CSL004", "Shader declared twice",
        "Shader '{0}' is declared in '{1}' and again here; only the first gets a wrapper",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TypedUavLoadFormat = new DiagnosticDescriptor(
        "CSL010", "Resource read and written through a RW view needs a 32-bit single-channel format",
        "Slot '{0}' is both read and written through its RW view; a typed UAV load is only allowed on R32_Float, R32_UInt and R32_SInt, so allocate it with float, uint or int rather than '{1}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Direct3D 11 only allows a typed unordered-access view to be loaded from when its format is R32_Float, R32_UInt or R32_SInt. " +
                     "A shader that reads its own RW output (Drawn |= on, InterlockedAdd on a texture) therefore needs a 32-bit single-channel resource.");

    // -- C# to SDSL --------------------------------------------------------------------------------

    public static readonly DiagnosticDescriptor ShaderNotPartial = new DiagnosticDescriptor(
        "CSL100", "Shader class must be partial",
        "Shader class '{0}' must be declared partial: the generator adds its keys and its SDSL source to it",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedSyntax = new DiagnosticDescriptor(
        "CSL101", "Not available in shader code",
        "Shader code cannot use {0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Shader methods are limited to what SDSL has: locals, the HLSL types and intrinsics, if/for/while/do/switch, and the operators.");

    public static readonly DiagnosticDescriptor UnsupportedType = new DiagnosticDescriptor(
        "CSL102", "Type not available in shader code",
        "Type '{0}' has no SDSL equivalent; use the Csl.Hlsl types, the primitives, or a struct nested in the shader",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ObjectCreation = new DiagnosticDescriptor(
        "CSL103", "No objects in shader code",
        "Shader code cannot create '{0}'; only the HLSL vector and matrix types and shader structs can be constructed",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedMember = new DiagnosticDescriptor(
        "CSL104", "Member not available in a shader",
        "A shader class cannot declare a {0}; use fields, methods and nested structs",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ShaderGeneric = new DiagnosticDescriptor(
        "CSL105", "Generic shader class",
        "Shader class '{0}' cannot be generic",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ShaderNested = new DiagnosticDescriptor(
        "CSL106", "Nested shader class",
        "Shader class '{0}' must be declared at namespace level",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedCall = new DiagnosticDescriptor(
        "CSL107", "Call not available in shader code",
        "Shader code cannot call '{0}'; only the intrinsics (Csl.Hlsl.Intrinsics), resource methods and shader methods are available",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BaseNotShader = new DiagnosticDescriptor(
        "CSL108", "Base is not a shader",
        "'{0}' is not a [Shader] class; a shader inherits shader classes only",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ShaderNameClash = new DiagnosticDescriptor(
        "CSL109", "Shader declared both in C# and in a .sdsl file",
        "Shader '{0}' is written in C# here and also exists as '{1}'; the .sdsl wins for keys and wrappers, so rename or remove one",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static Location FileLocation(string path, int line, int column)
    {
        var position = new LinePosition(System.Math.Max(0, line - 1), System.Math.Max(0, column - 1));
        return Location.Create(path, new TextSpan(0, 0), new LinePositionSpan(position, position));
    }
}
