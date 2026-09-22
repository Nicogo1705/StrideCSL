using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csl.Generators.CSharp;

/// <summary>
/// Turns a [Shader] partial class into SDSL. The subset of C# it accepts is what SDSL has: fields,
/// methods, nested structs, locals, the HLSL types and intrinsics, if/for/while/do/switch, and the
/// operators. Anything else is a diagnostic on the offending node, and the shader is not emitted.
/// </summary>
public sealed class ShaderTranslator
{
    private const string HlslNamespace = "Csl.Hlsl";
    private const string IntrinsicsType = "Csl.Hlsl.Intrinsics";

    private static readonly Dictionary<string, string> LoopMarkers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Unroll"] = "[unroll]",
        ["Loop"] = "[loop]",
        ["Branch"] = "[branch]",
        ["Flatten"] = "[flatten]",
    };

    private readonly INamedTypeSymbol shader;
    private readonly Compilation compilation;
    private readonly CancellationToken cancellation;
    private readonly TranslatedShader result;
    private readonly StringBuilder sb = new StringBuilder();
    private readonly Dictionary<SyntaxTree, SemanticModel> models = new Dictionary<SyntaxTree, SemanticModel>();
    private SemanticModel model = null!;
    private int indent;

    /// <summary>The [Mixin] shaders: their members are not in the input compilation (the stubs are generated), so names resolve here.</summary>
    private readonly List<INamedTypeSymbol> mixins = new List<INamedTypeSymbol>();

    private ShaderTranslator(INamedTypeSymbol shader, Compilation compilation, CancellationToken cancellation)
    {
        this.shader = shader;
        this.compilation = compilation;
        this.cancellation = cancellation;
        var attribute = shader.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == "Csl.ShaderAttribute");
        string? name = null;
        bool external = false;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Name" && named.Value.Value is string s) name = s;
            if (named.Key == "External" && named.Value.Value is bool b) external = b;
        }
        var ns = shader.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        var path = shader.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath ?? shader.Name + ".cs";
        result = new TranslatedShader(shader.Name, name ?? shader.Name, ns, path) { IsExternal = external };
    }

    public static TranslatedShader Translate(INamedTypeSymbol shader, Compilation compilation, CancellationToken cancellation)
    {
        var translator = new ShaderTranslator(shader, compilation, cancellation);
        translator.Run();
        return translator.result;
    }

    /// <summary>The SDSL name of a shader class: its [Shader(Name)] or its own name.</summary>
    public static string ShaderNameOf(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Csl.ShaderAttribute")
                continue;
            foreach (var named in attribute.NamedArguments)
                if (named.Key == "Name" && named.Value.Value is string s)
                    return s;
        }
        return type.Name;
    }

    public static bool IsShaderClass(ITypeSymbol? type) =>
        type != null && type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Csl.ShaderAttribute");

    // -- structure -----------------------------------------------------------------------------------

    private void Run()
    {
        var declarations = shader.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellation))
            .OfType<ClassDeclarationSyntax>()
            .OrderBy(d => d.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(d => d.SpanStart)
            .ToList();
        if (declarations.Count == 0)
            return;

        foreach (var declaration in declarations)
        {
            if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                Report(Diagnostics.ShaderNotPartial, declaration.Identifier.GetLocation(), shader.Name);
        }
        if (shader.IsGenericType)
            Report(Diagnostics.ShaderGeneric, declarations[0].Identifier.GetLocation(), shader.Name);
        if (shader.ContainingType != null)
            Report(Diagnostics.ShaderNested, declarations[0].Identifier.GetLocation(), shader.Name);

        foreach (var attribute in shader.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == "Csl.NumThreadsAttribute" && attribute.ConstructorArguments.Length == 3)
                result.NumThreads = ((int)attribute.ConstructorArguments[0].Value!, (int)attribute.ConstructorArguments[1].Value!, (int)attribute.ConstructorArguments[2].Value!);
        }

        if (result.IsExternal)
            return;

        // Header: namespace, name, bases.
        var bases = new List<string>();
        if (shader.BaseType != null && shader.BaseType.SpecialType != SpecialType.System_Object)
        {
            if (IsShaderClass(shader.BaseType))
                bases.Add(ShaderNameOf(shader.BaseType));
            else
                Report(Diagnostics.BaseNotShader, declarations[0].BaseList?.GetLocation() ?? declarations[0].Identifier.GetLocation(), shader.BaseType.ToDisplayString());
        }
        foreach (var attribute in shader.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Csl.MixinAttribute")
                continue;
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellation).GetLocation() ?? declarations[0].Identifier.GetLocation();
            foreach (var argument in attribute.ConstructorArguments.SelectMany(a => a.Kind == TypedConstantKind.Array ? a.Values : System.Collections.Immutable.ImmutableArray.Create(a)))
            {
                if (argument.Value is INamedTypeSymbol mixin && IsShaderClass(mixin))
                {
                    bases.Add(ShaderNameOf(mixin));
                    mixins.Add(mixin);
                }
                else
                {
                    Report(Diagnostics.BaseNotShader, location, argument.Value?.ToString() ?? "?");
                }
            }
        }
        if (mixins.Count > 0)
            result.MixinStubs = MixinStubEmitter.Emit(shader, mixins, result);

        if (result.Namespace != null)
        {
            Line("namespace " + result.Namespace);
            Line("{");
            indent++;
        }
        EmitDoc(declarations[0]);
        Line("shader " + result.ShaderName + (bases.Count > 0 ? " : " + string.Join(", ", bases) : string.Empty));
        Line("{");
        indent++;

        foreach (var declaration in declarations)
        {
            model = ModelFor(declaration.SyntaxTree);
            foreach (var member in declaration.Members)
            {
                cancellation.ThrowIfCancellationRequested();
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        EmitField(field);
                        break;
                    case MethodDeclarationSyntax method:
                        EmitMethod(method);
                        break;
                    case StructDeclarationSyntax nested:
                        EmitStruct(nested);
                        break;
                    default:
                        Report(Diagnostics.UnsupportedMember, member.GetLocation(), member.Kind().ToString().Replace("Declaration", string.Empty));
                        break;
                }
            }
        }

        indent--;
        Line("};");
        if (result.Namespace != null)
        {
            indent--;
            Line("}");
        }
        if (!result.HasErrors)
            result.Sdsl = sb.ToString();
    }

    /// <summary>A member of one of the [Mixin] shaders or their bases, by name.</summary>
    private ISymbol? MixinMember(string name)
    {
        foreach (var mixin in mixins)
        {
            for (var type = mixin; type != null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
            {
                foreach (var member in type.GetMembers(name))
                {
                    if (member.DeclaredAccessibility != Accessibility.Private)
                        return member;
                }
            }
        }
        return null;
    }

    private SemanticModel ModelFor(SyntaxTree tree)
    {
        if (!models.TryGetValue(tree, out var m))
        {
            m = compilation.GetSemanticModel(tree);
            models[tree] = m;
        }
        return m;
    }

    private void EmitStruct(StructDeclarationSyntax nested)
    {
        EmitDoc(nested);
        Line("struct " + nested.Identifier.Text);
        Line("{");
        indent++;
        foreach (var member in nested.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                var type = model.GetTypeInfo(field.Declaration.Type, cancellation).Type;
                var sdslType = SdslType(type, field.Declaration.Type);
                foreach (var variable in field.Declaration.Variables)
                    Line(sdslType + " " + variable.Identifier.Text + ";");
            }
            else
            {
                Report(Diagnostics.UnsupportedMember, member.GetLocation(), "struct " + member.Kind());
            }
        }
        indent--;
        Line("};");
    }

    private void EmitField(FieldDeclarationSyntax field)
    {
        var modifiers = new List<string>();
        var attributes = new List<string>();
        string? semantic = null;
        bool isStream = false, isStage = false, isCompose = false;
        foreach (var list in field.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                var name = model.GetTypeInfo(attribute, cancellation).Type?.ToDisplayString() ?? model.GetSymbolInfo(attribute, cancellation).Symbol?.ContainingType?.ToDisplayString();
                switch (name)
                {
                    case "Csl.StageAttribute": isStage = true; break;
                    case "Csl.StreamAttribute":
                        isStream = true;
                        if (attribute.ArgumentList?.Arguments.Count > 0 && model.GetConstantValue(attribute.ArgumentList.Arguments[0].Expression, cancellation).Value is string s)
                            semantic = s;
                        break;
                    case "Csl.ComposeAttribute": isCompose = true; break;
                    case "Csl.GroupSharedAttribute": modifiers.Add("groupshared"); break;
                    case "Csl.ColorAttribute": attributes.Add("[Color]"); break;
                    case "Csl.LinkAttribute":
                        if (attribute.ArgumentList?.Arguments.Count > 0 && model.GetConstantValue(attribute.ArgumentList.Arguments[0].Expression, cancellation).Value is string link)
                            attributes.Add("[Link(\"" + link + "\")]");
                        break;
                    default:
                        Report(Diagnostics.UnsupportedSyntax, attribute.GetLocation(), "attribute " + attribute.Name);
                        break;
                }
            }
        }
        bool isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
        bool isStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword);
        if (isConst || isStatic)
            modifiers.Insert(0, isConst ? "static const" : "static");
        if (isStage) modifiers.Insert(0, "stage");
        if (isStream) modifiers.Add("stream");
        if (isCompose) modifiers.Add("compose");

        var typeSymbol = model.GetTypeInfo(field.Declaration.Type, cancellation).Type;
        string type;
        if (isCompose)
        {
            type = IsShaderClass(typeSymbol) ? ShaderNameOf((INamedTypeSymbol)typeSymbol!) : Unsupported(field.Declaration.Type, typeSymbol);
        }
        else
        {
            type = SdslType(typeSymbol, field.Declaration.Type);
        }

        foreach (var variable in field.Declaration.Variables)
        {
            EmitDoc(field);
            foreach (var attribute in attributes)
                Line(attribute);
            var line = new StringBuilder();
            foreach (var modifier in modifiers)
                line.Append(modifier).Append(' ');
            line.Append(type).Append(' ').Append(variable.Identifier.Text);
            if (semantic != null)
                line.Append(" : ").Append(semantic);
            if (variable.Initializer != null)
                line.Append(" = ").Append(Expression(variable.Initializer.Value));
            line.Append(';');
            Line(line.ToString());
        }
    }

    private void EmitMethod(MethodDeclarationSyntax method)
    {
        var symbol = model.GetDeclaredSymbol(method, cancellation);
        if (symbol == null)
            return;
        if (symbol.IsGenericMethod)
            Report(Diagnostics.UnsupportedSyntax, method.Identifier.GetLocation(), "generic method");

        var header = new StringBuilder();
        if (method.Modifiers.Any(SyntaxKind.OverrideKeyword)) header.Append("override ");
        if (method.Modifiers.Any(SyntaxKind.AbstractKeyword)) header.Append("abstract ");
        header.Append(SdslType(symbol.ReturnType, method.ReturnType)).Append(' ').Append(method.Identifier.Text).Append('(');
        for (int i = 0; i < method.ParameterList.Parameters.Count; i++)
        {
            var parameter = method.ParameterList.Parameters[i];
            var parameterSymbol = symbol.Parameters[i];
            if (i > 0) header.Append(", ");
            switch (parameterSymbol.RefKind)
            {
                case RefKind.Ref: header.Append("inout "); break;
                case RefKind.Out: header.Append("out "); break;
                case RefKind.In: header.Append("in "); break;
            }
            header.Append(SdslType(parameterSymbol.Type, parameter.Type ?? (SyntaxNode)parameter)).Append(' ').Append(parameter.Identifier.Text);
            if (parameter.Default != null)
                Report(Diagnostics.UnsupportedSyntax, parameter.Default.GetLocation(), "default parameter value");
        }
        header.Append(')');

        EmitDoc(method);
        if (method.Body == null && method.ExpressionBody == null)
        {
            Line(header + ";");
            return;
        }
        Line(header.ToString());
        Line("{");
        indent++;
        if (method.Body != null)
        {
            foreach (var statement in method.Body.Statements)
                EmitStatement(statement);
        }
        else if (method.ExpressionBody != null)
        {
            var expression = Expression(method.ExpressionBody.Expression);
            Line(symbol.ReturnsVoid ? expression + ";" : "return " + expression + ";");
        }
        indent--;
        Line("}");
    }

    // -- statements ----------------------------------------------------------------------------------

    private void EmitStatement(StatementSyntax statement)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (statement)
        {
            case BlockSyntax block:
                Line("{");
                indent++;
                foreach (var inner in block.Statements)
                    EmitStatement(inner);
                indent--;
                Line("}");
                break;

            case LocalDeclarationStatementSyntax local:
                if (local.IsConst)
                    Report(Diagnostics.UnsupportedSyntax, local.GetLocation(), "const local");
                if (local.UsingKeyword != default)
                    Report(Diagnostics.UnsupportedSyntax, local.GetLocation(), "using declaration");
                Line(Declaration(local.Declaration) + ";");
                break;

            case ExpressionStatementSyntax expressionStatement:
                if (expressionStatement.Expression is InvocationExpressionSyntax invocation && TryLoopMarker(invocation, out var marker))
                {
                    Line(marker);
                    break;
                }
                Line(Expression(expressionStatement.Expression) + ";");
                break;

            case IfStatementSyntax ifStatement:
                Line("if (" + Expression(ifStatement.Condition) + ")");
                EmitBody(ifStatement.Statement);
                if (ifStatement.Else != null)
                {
                    if (ifStatement.Else.Statement is IfStatementSyntax elseIf)
                    {
                        Line("else if (" + Expression(elseIf.Condition) + ")");
                        EmitBody(elseIf.Statement);
                        var rest = elseIf.Else;
                        while (rest != null)
                        {
                            if (rest.Statement is IfStatementSyntax chained)
                            {
                                Line("else if (" + Expression(chained.Condition) + ")");
                                EmitBody(chained.Statement);
                                rest = chained.Else;
                            }
                            else
                            {
                                Line("else");
                                EmitBody(rest.Statement);
                                rest = null;
                            }
                        }
                    }
                    else
                    {
                        Line("else");
                        EmitBody(ifStatement.Else.Statement);
                    }
                }
                break;

            case ForStatementSyntax forStatement:
            {
                var head = new StringBuilder("for (");
                if (forStatement.Declaration != null)
                    head.Append(Declaration(forStatement.Declaration));
                else
                    head.Append(string.Join(", ", forStatement.Initializers.Select(Expression)));
                head.Append("; ");
                if (forStatement.Condition != null)
                    head.Append(Expression(forStatement.Condition));
                head.Append("; ");
                head.Append(string.Join(", ", forStatement.Incrementors.Select(Expression)));
                head.Append(')');
                Line(head.ToString());
                EmitBody(forStatement.Statement);
                break;
            }

            case WhileStatementSyntax whileStatement:
                Line("while (" + Expression(whileStatement.Condition) + ")");
                EmitBody(whileStatement.Statement);
                break;

            case DoStatementSyntax doStatement:
                Line("do");
                EmitBody(doStatement.Statement);
                Line("while (" + Expression(doStatement.Condition) + ");");
                break;

            case SwitchStatementSyntax switchStatement:
                Line("switch (" + Expression(switchStatement.Expression) + ")");
                Line("{");
                indent++;
                foreach (var section in switchStatement.Sections)
                {
                    foreach (var label in section.Labels)
                    {
                        switch (label)
                        {
                            case CaseSwitchLabelSyntax caseLabel:
                                Line("case " + Expression(caseLabel.Value) + ":");
                                break;
                            case DefaultSwitchLabelSyntax:
                                Line("default:");
                                break;
                            default:
                                Report(Diagnostics.UnsupportedSyntax, label.GetLocation(), "pattern case");
                                break;
                        }
                    }
                    indent++;
                    foreach (var inner in section.Statements)
                        EmitStatement(inner);
                    indent--;
                }
                indent--;
                Line("}");
                break;

            case BreakStatementSyntax:
                Line("break;");
                break;
            case ContinueStatementSyntax:
                Line("continue;");
                break;
            case ReturnStatementSyntax returnStatement:
                Line(returnStatement.Expression == null ? "return;" : "return " + Expression(returnStatement.Expression) + ";");
                break;
            case EmptyStatementSyntax:
                Line(";");
                break;
            default:
                Report(Diagnostics.UnsupportedSyntax, statement.GetLocation(), Describe(statement.Kind()));
                break;
        }
    }

    private void EmitBody(StatementSyntax statement)
    {
        if (statement is BlockSyntax)
        {
            EmitStatement(statement);
            return;
        }
        indent++;
        EmitStatement(statement);
        indent--;
    }

    private bool TryLoopMarker(InvocationExpressionSyntax invocation, out string marker)
    {
        marker = string.Empty;
        if (model.GetSymbolInfo(invocation, cancellation).Symbol is not IMethodSymbol method)
            return false;
        if (method.ContainingType?.ToDisplayString() != IntrinsicsType || !LoopMarkers.TryGetValue(method.Name, out var attribute))
            return false;
        if (method.Name == "Unroll" && invocation.ArgumentList.Arguments.Count == 1)
            attribute = "[unroll(" + Expression(invocation.ArgumentList.Arguments[0].Expression) + ")]";
        marker = attribute;
        return true;
    }

    private string Declaration(VariableDeclarationSyntax declaration)
    {
        var typeSymbol = model.GetTypeInfo(declaration.Type, cancellation).Type;
        if (declaration.Type.IsVar && declaration.Variables.Count == 1 && declaration.Variables[0].Initializer != null)
            typeSymbol = model.GetTypeInfo(declaration.Variables[0].Initializer!.Value, cancellation).ConvertedType ?? typeSymbol;
        var type = SdslType(typeSymbol, declaration.Type);
        var text = new StringBuilder(type).Append(' ');
        for (int i = 0; i < declaration.Variables.Count; i++)
        {
            var variable = declaration.Variables[i];
            if (i > 0) text.Append(", ");
            text.Append(variable.Identifier.Text);
            if (variable.Initializer != null)
                text.Append(" = ").Append(Expression(variable.Initializer.Value));
        }
        return text.ToString();
    }

    // -- expressions ---------------------------------------------------------------------------------

    private string Expression(ExpressionSyntax expression)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return Literal(literal);

            case ParenthesizedExpressionSyntax parenthesized:
                return "(" + Expression(parenthesized.Expression) + ")";

            case IdentifierNameSyntax identifier:
                return Identifier(identifier);

            case ThisExpressionSyntax:
                Report(Diagnostics.UnsupportedSyntax, expression.GetLocation(), "this");
                return "this";

            case BaseExpressionSyntax:
                return "base";

            case MemberAccessExpressionSyntax memberAccess:
                return MemberAccess(memberAccess);

            case InvocationExpressionSyntax invocation:
                return Invocation(invocation);

            case ElementAccessExpressionSyntax elementAccess:
                return Expression(elementAccess.Expression) + "[" + string.Join(", ", elementAccess.ArgumentList.Arguments.Select(a => Expression(a.Expression))) + "]";

            case ObjectCreationExpressionSyntax creation:
                return Creation(model.GetTypeInfo(creation, cancellation).Type, creation.ArgumentList, creation.Initializer, creation);

            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return Creation(model.GetTypeInfo(implicitCreation, cancellation).Type, implicitCreation.ArgumentList, implicitCreation.Initializer, implicitCreation);

            case CastExpressionSyntax cast:
            {
                var type = SdslType(model.GetTypeInfo(cast.Type, cancellation).Type, cast.Type);
                return "(" + type + ")" + Operand(cast.Expression, 14);
            }

            case PrefixUnaryExpressionSyntax prefix:
                return prefix.OperatorToken.Text + Operand(prefix.Operand, 14);

            case PostfixUnaryExpressionSyntax postfix:
                if (postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                    return Expression(postfix.Operand);
                return Operand(postfix.Operand) + postfix.OperatorToken.Text;

            case BinaryExpressionSyntax binary:
                if (binary.IsKind(SyntaxKind.IsExpression) || binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.CoalesceExpression))
                {
                    Report(Diagnostics.UnsupportedSyntax, binary.GetLocation(), Describe(binary.Kind()));
                    return "0";
                }
            {
                var precedence = Precedence(binary);
                return Operand(binary.Left, precedence) + " " + binary.OperatorToken.Text + " " + Operand(binary.Right, precedence, rightSide: true);
            }

            case AssignmentExpressionSyntax assignment:
                if (assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression))
                {
                    Report(Diagnostics.UnsupportedSyntax, assignment.GetLocation(), "??=");
                    return "0";
                }
                return Expression(assignment.Left) + " " + assignment.OperatorToken.Text + " " + Expression(assignment.Right);

            case ConditionalExpressionSyntax conditional:
                return Operand(conditional.Condition, 2) + " ? " + Operand(conditional.WhenTrue, 1) + " : " + Operand(conditional.WhenFalse, 1);

            case DefaultExpressionSyntax defaultExpression:
            {
                var type = SdslType(model.GetTypeInfo(defaultExpression, cancellation).Type, defaultExpression);
                return "(" + type + ")0";
            }

            case CheckedExpressionSyntax checkedExpression:
                return Expression(checkedExpression.Expression);

            default:
                Report(Diagnostics.UnsupportedSyntax, expression.GetLocation(), Describe(expression.Kind()));
                return "0";
        }
    }

    /// <summary>
    /// An operand of an operator, parenthesised when C# needed no parentheses but reading it back
    /// would: HLSL has C's precedence, which is C#'s, so only a lower-precedence child needs them.
    /// </summary>
    private string Operand(ExpressionSyntax expression, int parentPrecedence, bool rightSide = false)
    {
        var text = Expression(expression);
        var precedence = Precedence(expression);
        if (precedence < parentPrecedence || (rightSide && precedence == parentPrecedence && parentPrecedence < 14))
            return "(" + text + ")";
        return text;
    }

    private string Operand(ExpressionSyntax expression) => Operand(expression, 15);

    /// <summary>C precedence, higher binds tighter. Primaries are 16, unary 15.</summary>
    private static int Precedence(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case ConditionalExpressionSyntax: return 1;
            case AssignmentExpressionSyntax: return 0;
            case CastExpressionSyntax:
            case PrefixUnaryExpressionSyntax: return 14;
            case BinaryExpressionSyntax binary:
                switch (binary.Kind())
                {
                    case SyntaxKind.LogicalOrExpression: return 2;
                    case SyntaxKind.LogicalAndExpression: return 3;
                    case SyntaxKind.BitwiseOrExpression: return 4;
                    case SyntaxKind.ExclusiveOrExpression: return 5;
                    case SyntaxKind.BitwiseAndExpression: return 6;
                    case SyntaxKind.EqualsExpression:
                    case SyntaxKind.NotEqualsExpression: return 7;
                    case SyntaxKind.LessThanExpression:
                    case SyntaxKind.GreaterThanExpression:
                    case SyntaxKind.LessThanOrEqualExpression:
                    case SyntaxKind.GreaterThanOrEqualExpression: return 8;
                    case SyntaxKind.LeftShiftExpression:
                    case SyntaxKind.RightShiftExpression: return 9;
                    case SyntaxKind.AddExpression:
                    case SyntaxKind.SubtractExpression: return 10;
                    default: return 11;
                }
            default: return 16;
        }
    }

    private string Literal(LiteralExpressionSyntax literal)
    {
        switch (literal.Kind())
        {
            case SyntaxKind.TrueLiteralExpression: return "true";
            case SyntaxKind.FalseLiteralExpression: return "false";
            case SyntaxKind.NumericLiteralExpression:
            {
                var text = literal.Token.Text.Replace("_", string.Empty);
                var value = literal.Token.Value;
                switch (value)
                {
                    case float:
                    case double:
                    {
                        // Keep the digits as written, drop the C# suffix, make sure it reads as a real.
                        var digits = text.TrimEnd('f', 'F', 'd', 'D', 'm', 'M');
                        if (digits.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
                            digits += ".0";
                        return digits;
                    }
                    case uint:
                        // The engine's SDSL parser takes no u suffix; the value is what matters.
                        return text.TrimEnd('u', 'U');
                    case int:
                        return text;
                    default:
                        Report(Diagnostics.UnsupportedSyntax, literal.GetLocation(), "literal of type " + value?.GetType().Name);
                        return text;
                }
            }
            default:
                Report(Diagnostics.UnsupportedSyntax, literal.GetLocation(), Describe(literal.Kind()));
                return "0";
        }
    }

    private string Identifier(IdentifierNameSyntax identifier)
    {
        var symbol = model.GetSymbolInfo(identifier, cancellation).Symbol;
        switch (symbol)
        {
            case IFieldSymbol field:
                return FieldReference(field, identifier);
            case ILocalSymbol:
            case IParameterSymbol:
                return identifier.Identifier.Text;
            case IMethodSymbol:
                return identifier.Identifier.Text;
            case IPropertySymbol property when property.ContainingType?.ContainingNamespace?.ToDisplayString() == HlslNamespace:
                return identifier.Identifier.Text;
            case null:
                return MixinMember(identifier.Identifier.Text) is IFieldSymbol mixinField
                    ? FieldReference(mixinField, identifier)
                    : identifier.Identifier.Text;
            default:
                Report(Diagnostics.UnsupportedSyntax, identifier.GetLocation(), symbol.Kind.ToString().ToLowerInvariant() + " " + identifier.Identifier.Text);
                return identifier.Identifier.Text;
        }
    }

    private string FieldReference(IFieldSymbol field, SyntaxNode at)
    {
        if (field.ContainingType == null || !IsShaderClass(field.ContainingType))
        {
            if (field.ContainingType?.TypeKind == TypeKind.Struct)
                return field.Name;
            Report(Diagnostics.UnsupportedSyntax, at.GetLocation(), "field " + field.ToDisplayString());
            return field.Name;
        }
        bool isStream = field.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Csl.StreamAttribute");
        return isStream ? "streams." + field.Name : field.Name;
    }

    private string MemberAccess(MemberAccessExpressionSyntax memberAccess)
    {
        var symbol = model.GetSymbolInfo(memberAccess, cancellation).Symbol;
        var name = memberAccess.Name.Identifier.Text;

        if (memberAccess.Expression is ThisExpressionSyntax)
        {
            if (symbol == null)
                symbol = MixinMember(name);
            return symbol is IFieldSymbol thisField ? FieldReference(thisField, memberAccess) : name;
        }
        if (memberAccess.Expression is BaseExpressionSyntax)
            return "base." + name;

        // A static member of a type: only the intrinsics and the shader's own statics.
        if (model.GetSymbolInfo(memberAccess.Expression, cancellation).Symbol is INamedTypeSymbol type)
        {
            if (type.ToDisplayString() == IntrinsicsType)
                return name;
            if (IsShaderClass(type) && symbol is IFieldSymbol staticField)
                return FieldReference(staticField, memberAccess);
            Report(Diagnostics.UnsupportedCall, memberAccess.GetLocation(), type.ToDisplayString() + "." + name);
            return name;
        }

        switch (symbol)
        {
            case IPropertySymbol property when property.ContainingType?.ContainingNamespace?.ToDisplayString() == HlslNamespace:
                // A swizzle or a resource member.
                return Expression(memberAccess.Expression) + "." + name;
            case IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Struct:
                return Expression(memberAccess.Expression) + "." + name;
            case IMethodSymbol:
                return Expression(memberAccess.Expression) + "." + name;
            case IFieldSymbol field when IsShaderClass(field.ContainingType):
                // compose.Member
                return Expression(memberAccess.Expression) + "." + name;
            default:
                Report(Diagnostics.UnsupportedSyntax, memberAccess.GetLocation(), "member " + name + (symbol == null ? string.Empty : " of " + symbol.ContainingType?.ToDisplayString()));
                return Expression(memberAccess.Expression) + "." + name;
        }
    }

    private string Invocation(InvocationExpressionSyntax invocation)
    {
        var symbol = model.GetSymbolInfo(invocation, cancellation).Symbol as IMethodSymbol;
        var arguments = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => Expression(a.Expression)));
        if (symbol == null)
        {
            // A method of a [Mixin] shader: its stub is generated, so the input compilation has no symbol for it.
            var calledName = invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access => access.Name.Identifier.Text,
                _ => null,
            };
            if (calledName != null && MixinMember(calledName) is IMethodSymbol)
                return calledName + "(" + arguments + ")";
            Report(Diagnostics.UnsupportedCall, invocation.GetLocation(), invocation.Expression.ToString());
            return invocation.Expression + "(" + arguments + ")";
        }

        var owner = symbol.ContainingType;
        if (owner.ToDisplayString() == IntrinsicsType)
        {
            if (LoopMarkers.ContainsKey(symbol.Name))
            {
                Report(Diagnostics.UnsupportedSyntax, invocation.GetLocation(), symbol.Name + "() anywhere but as a statement before a loop or an if");
                return string.Empty;
            }
            return symbol.Name + "(" + arguments + ")";
        }
        if (owner.ContainingNamespace?.ToDisplayString() == HlslNamespace)
        {
            // Texture.Load(...), Buffer.GetDimensions(...): a member of a resource.
            var target = invocation.Expression is MemberAccessExpressionSyntax access ? Expression(access.Expression) + "." : string.Empty;
            return target + symbol.Name + "(" + arguments + ")";
        }
        if (IsShaderClass(owner))
        {
            string prefix = string.Empty;
            if (invocation.Expression is MemberAccessExpressionSyntax access)
            {
                if (access.Expression is BaseExpressionSyntax)
                    prefix = "base.";
                else if (access.Expression is not ThisExpressionSyntax)
                    prefix = Expression(access.Expression) + ".";
            }
            return prefix + symbol.Name + "(" + arguments + ")";
        }
        Report(Diagnostics.UnsupportedCall, invocation.GetLocation(), symbol.ToDisplayString());
        return symbol.Name + "(" + arguments + ")";
    }

    private string Creation(ITypeSymbol? type, ArgumentListSyntax? arguments, InitializerExpressionSyntax? initializer, SyntaxNode at)
    {
        if (initializer != null)
            Report(Diagnostics.UnsupportedSyntax, initializer.GetLocation(), "object initializer");
        if (type == null || type.TypeKind != TypeKind.Struct)
        {
            Report(Diagnostics.ObjectCreation, at.GetLocation(), type?.ToDisplayString() ?? "?");
            return "0";
        }
        var sdslType = SdslType(type, at);
        if (arguments == null || arguments.Arguments.Count == 0)
            return "(" + sdslType + ")0";
        return sdslType + "(" + string.Join(", ", arguments.Arguments.Select(a => Expression(a.Expression))) + ")";
    }

    // -- types ---------------------------------------------------------------------------------------

    private string SdslType(ITypeSymbol? type, SyntaxNode at)
    {
        if (type == null)
        {
            Report(Diagnostics.UnsupportedType, at.GetLocation(), "?");
            return "?";
        }
        switch (type.SpecialType)
        {
            case SpecialType.System_Void: return "void";
            case SpecialType.System_Boolean: return "bool";
            case SpecialType.System_Int32: return "int";
            case SpecialType.System_UInt32: return "uint";
            case SpecialType.System_Single: return "float";
            case SpecialType.System_Double: return "double";
            case SpecialType.System_Int64: return "int64_t";
            case SpecialType.System_UInt64: return "uint64_t";
        }
        if (type is INamedTypeSymbol named)
        {
            if (named.ContainingNamespace?.ToDisplayString() == HlslNamespace)
            {
                if (named.IsGenericType)
                    return named.Name + "<" + string.Join(", ", named.TypeArguments.Select(t => SdslType(t, at))) + ">";
                return named.Name;
            }
            if (named.TypeKind == TypeKind.Struct && SymbolEqualityComparer.Default.Equals(named.ContainingType, shader))
                return named.Name;
            if (IsShaderClass(named))
                return ShaderNameOf(named);
        }
        return Unsupported(at, type);
    }

    private string Unsupported(SyntaxNode at, ITypeSymbol? type)
    {
        Report(Diagnostics.UnsupportedType, at.GetLocation(), type?.ToDisplayString() ?? "?");
        return type?.Name ?? "?";
    }

    // -- output --------------------------------------------------------------------------------------

    private void Line(string text)
    {
        for (int i = 0; i < indent; i++)
            sb.Append("    ");
        sb.Append(text).Append('\n');
    }

    /// <summary>The /// comment of a member, as /// lines: the wrapper's documentation comes back from the SDSL.</summary>
    private void EmitDoc(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia();
        foreach (var piece in trivia)
        {
            if (!piece.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !(piece.IsKind(SyntaxKind.SingleLineCommentTrivia) && piece.ToString().StartsWith("///", StringComparison.Ordinal)))
                continue;
            var text = piece.ToFullString();
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("///", StringComparison.Ordinal))
                    line = line.Substring(3).Trim();
                if (line.Length == 0 || line == "<summary>" || line == "</summary>")
                    continue;
                line = line.Replace("<summary>", string.Empty).Replace("</summary>", string.Empty).Trim();
                if (line.Length > 0)
                    Line("/// " + line);
            }
        }
    }

    private void Report(DiagnosticDescriptor descriptor, Location location, params object[] args)
        => result.Diagnostics.Add(Diagnostic.Create(descriptor, location, args));

    private static string Describe(SyntaxKind kind)
    {
        var text = kind.ToString();
        if (text.EndsWith("Expression", StringComparison.Ordinal)) text = text.Substring(0, text.Length - "Expression".Length) + " expression";
        else if (text.EndsWith("Statement", StringComparison.Ordinal)) text = text.Substring(0, text.Length - "Statement".Length) + " statement";
        return char.ToLowerInvariant(text[0]) + text.Substring(1);
    }
}
