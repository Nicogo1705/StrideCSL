using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Csl.Generators;

/// <summary>
/// Checks calls to the typed allocation helpers (Csl.Textures.New3D&lt;T&gt;(..., slots)): a slot the
/// shader both reads and writes through a RW view needs a 32-bit single-channel element type.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TypedUavFormatAnalyzer : DiagnosticAnalyzer
{
    private const int ReadWriteAccess = 3;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Diagnostics.TypedUavLoadFormat);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (!method.IsGenericMethod || method.TypeArguments.Length != 1)
            return;
        var owner = method.ContainingType;
        if (owner == null || owner.ContainingNamespace?.ToDisplayString() != "Csl" || (owner.Name != "Textures" && owner.Name != "Buffers"))
            return;

        var element = method.TypeArguments[0];
        var elementName = element.SpecialType switch
        {
            SpecialType.System_Single => "float",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int32 => "int",
            _ => element.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        };
        if (elementName is "float" or "uint" or "int")
            return;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (context.SemanticModel.GetSymbolInfo(argument.Expression, context.CancellationToken).Symbol is not IFieldSymbol field)
                continue;
            var slot = field.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "SlotAttribute" && a.AttributeClass.ContainingNamespace?.ToDisplayString() == "Csl");
            if (slot == null || slot.ConstructorArguments.Length < 2)
                continue;
            if (slot.ConstructorArguments[0].Value is not int access || access != ReadWriteAccess)
                continue;
            // ResourceKind.Texture = 0, TypedBuffer = 1: the typed views. Structured and raw buffers are not formatted.
            if (slot.ConstructorArguments[1].Value is not int kind || kind > 1)
                continue;
            var location = invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
                ? generic.TypeArgumentList.GetLocation()
                : invocation.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.TypedUavLoadFormat, location, field.Name, elementName));
        }
    }
}
