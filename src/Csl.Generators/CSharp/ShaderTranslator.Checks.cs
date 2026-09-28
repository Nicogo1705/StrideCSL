using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csl.Generators.CSharp;

/// <summary>
/// What the engine's compiler, or Direct3D 11's, refuses in code C# accepts, reported on the C# line
/// rather than as a SPIR-V validation error or a parse error elsewhere, or not at all. Each check was
/// seen failing on Stride 4.4.
/// </summary>
public sealed partial class ShaderTranslator
{
    /// <summary>
    /// Identifiers the engine's SDSL parser takes for keywords or types: a local named <c>sample</c> or
    /// <c>float2</c> fails with "Unexpected token : override" several lines away. The vector and matrix
    /// type names are matched by <see cref="HlslTypeName"/>. Measured by <c>Csl.TestApp names</c>: each
    /// fails as a local, a parameter, a method, a shader variable and a struct field.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        // HLSL and SDSL keywords C# does not reserve.
        "sample", "point", "line", "triangle", "lineadj", "triangleadj", "linear", "centroid", "precise",
        "shared", "groupshared", "uniform", "register", "packoffset", "inout", "inline", "export", "compile",
        "compile_fragment", "technique", "technique10", "technique11", "pass", "stateblock", "stateblock_state",
        "pixelfragment", "vertexfragment", "typedef", "unsigned", "nointerpolation", "noperspective", "row_major",
        "column_major", "snorm", "unorm", "discard", "vector", "matrix", "texture", "sampler", "cbuffer", "tbuffer",
        "rgroup", "fxgroup", "asm", "asm_fragment", "NULL", "stream", "shader", "compose", "foreach",
        // C# keywords, reachable as @name, that SDSL reserves as well.
        "in", "out", "struct", "class", "interface", "static", "const", "extern", "volatile", "namespace", "string",
        "var", "void", "return", "true", "false", "if", "else", "for", "do", "while", "switch", "case", "default",
        "break", "continue",
        // HLSL object types.
        "Buffer", "RWBuffer", "StructuredBuffer", "RWStructuredBuffer", "ByteAddressBuffer", "RWByteAddressBuffer",
        "AppendStructuredBuffer", "ConsumeStructuredBuffer", "SamplerState", "SamplerComparisonState",
        "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray",
        "Texture3D", "TextureCube", "TextureCubeArray", "RWTexture1D", "RWTexture1DArray", "RWTexture2D",
        "RWTexture2DArray", "RWTexture3D", "InputPatch", "OutputPatch", "PointStream", "LineStream",
        "TriangleStream", "BlendState", "DepthStencilState", "RasterizerState", "DepthStencilView",
        "RenderTargetView", "PixelShader", "VertexShader", "GeometryShader", "HullShader", "DomainShader",
        "ComputeShader", "CompileShader",
    };

    /// <summary>
    /// Names SDSL gives a value of its own (the shader, its base, the streams): fine for a method or a
    /// struct field, not for a local, a parameter or a shader variable.
    /// </summary>
    private static readonly HashSet<string> ReservedValueNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "base", "this", "streams",
    };
    private static readonly Regex HlslTypeName = new Regex(
        @"^(bool|int|uint|dword|half|float|double|min16float|min10float|min16int|min12int|min16uint)([1-4](x[1-4])?)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Only a pixel shader has neighbours to take derivatives from, or a pixel to discard.</summary>
    private static readonly HashSet<string> PixelOnlyIntrinsics = new HashSet<string>(StringComparer.Ordinal)
    {
        "ddx", "ddy", "ddx_coarse", "ddx_fine", "ddy_coarse", "ddy_fine", "fwidth", "discard", "clip",
    };

    /// <summary>Resource methods that pick the mip level from derivatives.</summary>
    private static readonly HashSet<string> ImplicitLevelMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "Sample", "SampleBias", "SampleCmp", "CalculateLevelOfDetail", "CalculateLevelOfDetailUnclamped",
    };

    private void RunChecks(List<ClassDeclarationSyntax> declarations)
    {
        CheckReservedName(ShaderNameOf(shader), declarations[0].Identifier.GetLocation());
        CheckThreadGroup(declarations[0]);
        CheckEngineNameClash(declarations[0]);
        bool isCompute = ReachesComputeShaderBase(shader);
        var calls = new Dictionary<IMethodSymbol, List<(IMethodSymbol Callee, Location At)>>(SymbolEqualityComparer.Default);
        var methods = new Dictionary<IMethodSymbol, MethodDeclarationSyntax>(SymbolEqualityComparer.Default);
        foreach (var declaration in declarations)
        {
            var checkModel = ModelFor(declaration.SyntaxTree);
            foreach (var node in declaration.DescendantNodes())
            {
                cancellation.ThrowIfCancellationRequested();
                switch (node)
                {
                    case VariableDeclaratorSyntax variable:
                        // A struct field is reached through its struct: only the keywords matter.
                        CheckReservedName(variable.Identifier.ValueText, variable.Identifier.GetLocation(), isValue: variable.FirstAncestorOrSelf<StructDeclarationSyntax>() == null);
                        break;
                    case ParameterSyntax parameter:
                        CheckReservedName(parameter.Identifier.ValueText, parameter.Identifier.GetLocation(), isValue: true);
                        break;
                    case SingleVariableDesignationSyntax designation:
                        CheckReservedName(designation.Identifier.ValueText, designation.Identifier.GetLocation(), isValue: true);
                        break;
                    case StructDeclarationSyntax structure:
                        CheckReservedName(structure.Identifier.ValueText, structure.Identifier.GetLocation());
                        break;
                    case MethodDeclarationSyntax method:
                        CheckReservedName(method.Identifier.ValueText, method.Identifier.GetLocation());
                        if (checkModel.GetDeclaredSymbol(method, cancellation) is { } symbol)
                            methods[symbol] = method;
                        break;
                    case AssignmentExpressionSyntax assignment:
                        CheckParameterWrite(assignment.Left, checkModel);
                        break;
                    case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                        CheckParameterWrite(prefix.Operand, checkModel);
                        break;
                    case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                        CheckParameterWrite(postfix.Operand, checkModel);
                        break;
                    case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword):
                        CheckParameterWrite(argument.Expression, checkModel);
                        break;
                    case ReturnStatementSyntax ret when InsideLoop(ret):
                        Report(Diagnostics.ReturnInLoop, ret.ReturnKeyword.GetLocation());
                        break;
                    case ObjectCreationExpressionSyntax creation:
                        CheckIntegerDivisionInFloatVector(creation, creation.ArgumentList, checkModel);
                        break;
                    case ImplicitObjectCreationExpressionSyntax creation:
                        CheckIntegerDivisionInFloatVector(creation, creation.ArgumentList, checkModel);
                        break;
                    case InvocationExpressionSyntax invocation:
                        if (checkModel.GetSymbolInfo(invocation, cancellation).Symbol is not IMethodSymbol callee)
                            break;
                        if (isCompute && IsPixelOnly(callee))
                            Report(Diagnostics.PixelOnlyInCompute, invocation.GetLocation(), callee.Name);
                        // base.F() and Sdsl.Base(this).F() reach the overridden method, not this one.
                        if (!CallsBase(invocation, checkModel)
                            && invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>() is { } caller
                            && checkModel.GetDeclaredSymbol(caller, cancellation) is { } callerSymbol
                            && SymbolEqualityComparer.Default.Equals(callee.OriginalDefinition.ContainingType, shader))
                        {
                            if (!calls.TryGetValue(callerSymbol, out var list))
                                calls[callerSymbol] = list = new List<(IMethodSymbol, Location)>();
                            list.Add((callee.OriginalDefinition, invocation.GetLocation()));
                        }
                        break;
                }
            }
        }
        CheckRecursion(methods, calls);
        if (isCompute && !shader.IsAbstract && result.NumThreads == null)
            Report(Diagnostics.NoNumThreads, declarations[0].Identifier.GetLocation(), shader.Name);
    }

    private bool CallsBase(InvocationExpressionSyntax invocation, SemanticModel checkModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access)
            return false;
        if (access.Expression is BaseExpressionSyntax)
            return true;
        return access.Expression is InvocationExpressionSyntax inner
            && checkModel.GetSymbolInfo(inner, cancellation).Symbol is IMethodSymbol { Name: "Base" } marker
            && marker.ContainingType?.ToDisplayString() == SdslType;
    }

    private void CheckReservedName(string name, Location location, bool isValue = false)
    {
        if (ReservedNames.Contains(name) || HlslTypeName.IsMatch(name) || (isValue && ReservedValueNames.Contains(name)))
            Report(Diagnostics.ReservedName, location, name);
    }

    private static bool ReachesComputeShaderBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
            if (IsShaderClass(current) && ShaderNameOf(current) == "ComputeShaderBase")
                return true;
        return false;
    }

    private static bool IsPixelOnly(IMethodSymbol method)
    {
        var owner = method.ContainingType?.ToDisplayString();
        if (owner == IntrinsicsType)
            return PixelOnlyIntrinsics.Contains(method.Name);
        return method.ContainingType?.ContainingNamespace?.ToDisplayString() == TypesNamespace && ImplicitLevelMethods.Contains(method.Name);
    }

    /// <summary>A return inside a loop of the same method: not one inside a nested function.</summary>
    private static bool InsideLoop(SyntaxNode node)
    {
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
        {
            switch (parent)
            {
                case ForStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case ForEachStatementSyntax:
                    return true;
                case MemberDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case AnonymousFunctionExpressionSyntax:
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// A write whose target is a shader parameter: a field of a shader that is not a stream, not static,
    /// not groupshared. Writing an element of a RW resource is what resources are for.
    /// </summary>
    private void CheckParameterWrite(ExpressionSyntax target, SemanticModel checkModel)
    {
        var root = target;
        while (true)
        {
            switch (root)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    root = parenthesized.Expression;
                    continue;
                case ElementAccessExpressionSyntax element:
                    if (IsResource(checkModel.GetTypeInfo(element.Expression, cancellation).Type))
                        return;
                    root = element.Expression;
                    continue;
                case MemberAccessExpressionSyntax member when member.Expression is not ThisExpressionSyntax
                        && member.Expression is not IdentifierNameSyntax { Identifier.ValueText: "streams" }
                        && checkModel.GetSymbolInfo(member, cancellation).Symbol is not IFieldSymbol { ContainingType.TypeKind: TypeKind.Class }:
                    // A swizzle or a struct member: the write lands in what it is taken from.
                    root = member.Expression;
                    continue;
            }
            break;
        }
        if (checkModel.GetSymbolInfo(root, cancellation).Symbol is not IFieldSymbol field
            || field.IsStatic || field.IsConst || !IsShaderClass(field.ContainingType) || IsStream(field))
            return;
        foreach (var attribute in field.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString();
            if (name == "Csl.GroupSharedAttribute")
                return;
            if (name == "Csl.ModifiersAttribute" && attribute.ConstructorArguments.Any(a => a.Value is string s && (s.Contains("static") || s.Contains("groupshared"))))
                return;
        }
        Report(Diagnostics.ParameterWrite, target.GetLocation(), field.Name);
    }

    private static bool IsResource(ITypeSymbol? type) =>
        type != null
        && type.ContainingNamespace?.ToDisplayString() == TypesNamespace
        && Regex.IsMatch(type.Name, "^(RW|RasterizerOrdered|Append|Consume)?(Texture|Buffer|StructuredBuffer|ByteAddressBuffer)");

    /// <summary>
    /// <c>new float3(i / 4, 0, 0)</c> with an integer <c>i</c>: Stride 4.4 divides in float
    /// (stride3d/stride#3468). The same division through a local is right.
    /// </summary>
    private void CheckIntegerDivisionInFloatVector(ExpressionSyntax creation, ArgumentListSyntax? arguments, SemanticModel checkModel)
    {
        if (arguments == null
            || checkModel.GetTypeInfo(creation, cancellation).Type is not INamedTypeSymbol type
            || type.ContainingNamespace?.ToDisplayString() != TypesNamespace
            || !Regex.IsMatch(type.Name, "^(float|half|double)[1-4]"))
            return;
        foreach (var argument in arguments.Arguments)
        {
            var expression = argument.Expression;
            while (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;
            if (expression is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.DivideExpression } division
                && checkModel.GetTypeInfo(division, cancellation).Type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32)
                Report(Diagnostics.IntegerDivisionInFloatVector, division.GetLocation(), division.ToString(), type.Name);
        }
    }

    private void CheckRecursion(Dictionary<IMethodSymbol, MethodDeclarationSyntax> methods, Dictionary<IMethodSymbol, List<(IMethodSymbol Callee, Location At)>> calls)
    {
        foreach (var start in methods.Keys)
        {
            // The shortest way back to start, if any.
            var previous = new Dictionary<IMethodSymbol, IMethodSymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<IMethodSymbol>();
            queue.Enqueue(start);
            IMethodSymbol? last = null;
            while (queue.Count > 0 && last == null)
            {
                var current = queue.Dequeue();
                if (!calls.TryGetValue(current, out var callees))
                    continue;
                foreach (var (callee, _) in callees)
                {
                    if (SymbolEqualityComparer.Default.Equals(callee, start))
                    {
                        last = current;
                        break;
                    }
                    if (!previous.ContainsKey(callee) && !SymbolEqualityComparer.Default.Equals(callee, start))
                    {
                        previous[callee] = current;
                        queue.Enqueue(callee);
                    }
                }
            }
            if (last == null)
                continue;
            var path = new List<string> { start.Name };
            for (var step = last; !SymbolEqualityComparer.Default.Equals(step, start); step = previous[step])
                path.Insert(1, step.Name);
            path.Add(start.Name);
            Report(Diagnostics.Recursion, methods[start].Identifier.GetLocation(), start.Name, string.Join(" → ", path));
        }
    }

    private void CheckThreadGroup(ClassDeclarationSyntax declaration)
    {
        if (result.NumThreads is not { } threads)
            return;
        var location = shader.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Csl.NumThreadsAttribute")
            ?.ApplicationSyntaxReference?.GetSyntax(cancellation).GetLocation() ?? declaration.Identifier.GetLocation();
        long total = (long)threads.X * threads.Y * threads.Z;
        if (threads.X < 1 || threads.Y < 1 || threads.Z < 1 || threads.X > 1024 || threads.Y > 1024 || threads.Z > 64 || total > 1024)
            Report(Diagnostics.ThreadGroupSize, location, threads.X, threads.Y, threads.Z, total);
    }

    /// <summary>A C# shader named like an engine shader replaces it in every effect, unless it says it means to.</summary>
    private void CheckEngineNameClash(ClassDeclarationSyntax declaration)
    {
        var name = ShaderNameOf(shader);
        var attribute = shader.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == "Csl.ShaderAttribute");
        if (attribute.NamedArguments.Any(a => a.Key == "Replaces" && a.Value.Value is true))
            return;
        var engine = compilation.GetTypeByMetadataName("Csl.Engine." + name);
        if (engine != null && !SymbolEqualityComparer.Default.Equals(engine, shader) && IsShaderClass(engine))
            Report(Diagnostics.ReplacesEngineShader, declaration.Identifier.GetLocation(), name);
    }
}
