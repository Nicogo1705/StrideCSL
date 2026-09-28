using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Csl.Generators.Conversion;

/// <summary>
/// Makes explicit, in C# converted from SDSL, what HLSL does implicitly: a narrowing conversion
/// becomes a cast, an int used as a condition a comparison with zero, a local read before any
/// assignment gets <c>= default</c> (which the translator writes back as no initializer), a name
/// that is not declared anywhere a macro. Each fix answers one compiler error; the SDSL the C#
/// translates back to means the same as the original.
/// </summary>
public static class CSharpFixer
{
    private static readonly Regex Quoted = new Regex("'([^']*)'", RegexOptions.Compiled);
    private static readonly Regex MacroLike = new Regex(@"^[A-Z_][A-Z0-9_]*$", RegexOptions.Compiled);

    public static SyntaxTree Fix(SyntaxTree tree, SemanticModel model, List<Diagnostic> errors, out int applied, CancellationToken cancellation)
    {
        var root = tree.GetRoot(cancellation);
        var changes = new List<TextChange>();
        var renames = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);

        foreach (var error in errors.OrderBy(e => e.Location.SourceSpan.Start))
        {
            var span = error.Location.SourceSpan;
            var node = root.FindNode(span, getInnermostNodeForTie: true);
            var message = error.GetMessage(CultureInfo.InvariantCulture);
            var quoted = Quoted.Matches(message).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            TextChange? change = null;

            switch (error.Id)
            {
                // Cannot implicitly convert type 'A' to 'B'
                case "CS0266":
                case "CS0029":
                    if (quoted.Count >= 2 && Expression(node, span) is { } converted)
                    {
                        // uint += uint * int: C# widens the right side to long, HLSL keeps it uint.
                        if (converted is AssignmentExpressionSyntax compound && !compound.IsKind(SyntaxKind.SimpleAssignmentExpression))
                            converted = compound.Right;
                        change = Convert(converted, quoted[0], quoted[1], model, cancellation);
                    }
                    break;

                // Argument n: cannot convert from 'A' to 'B'. Only a change of scalar type is safe to make
                // explicit: the compiler names the candidate it liked best, which may not be the overload
                // HLSL picks, and a cast to its vector size would change the values.
                case "CS1503":
                    if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>() is { } mulCall && FixMul(mulCall, model, cancellation) is { } mulChange)
                    {
                        change = mulChange;
                        break;
                    }
                    if (quoted.Count >= 2 && (SameShape(quoted[0], quoted[1]) || (Truncates(quoted[0], quoted[1]) && SingleCandidate(node, model, cancellation))))
                    {
                        if (node is ArgumentSyntax argument && argument.RefKindKeyword.IsKind(SyntaxKind.None))
                            change = Convert(argument.Expression, quoted[0], quoted[1], model, cancellation);
                        else if (Expression(node, span) is { } argumentExpression && argumentExpression.Parent is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.None })
                            change = Convert(argumentExpression, quoted[0], quoted[1], model, cancellation);
                    }
                    break;

                // Argument n must be passed with the 'ref' / 'out' keyword: SDSL writes none at the call.
                case "CS1620":
                    if (quoted.Count >= 1 && (quoted[0] == "ref" || quoted[0] == "out"))
                    {
                        var target = node as ArgumentSyntax ?? node.FirstAncestorOrSelf<ArgumentSyntax>();
                        if (target != null && target.RefKindKeyword.IsKind(SyntaxKind.None))
                            change = new TextChange(new TextSpan(target.Expression.SpanStart, 0), quoted[0] + " ");
                        else if (target != null)
                            change = new TextChange(target.RefKindKeyword.Span, quoted[0]);
                    }
                    break;

                // An out parameter read before it is written, or not written on every path: HLSL allows
                // both. Sdsl.Undefined(out x) at the start satisfies C# and leaves nothing in the SDSL.
                case "CS0269":
                case "CS0177":
                {
                    var parameterSymbol = model.GetSymbolInfo(node, cancellation).Symbol as IParameterSymbol
                        ?? (quoted.Count >= 1 ? model.GetEnclosingSymbol(span.Start, cancellation) is IMethodSymbol enclosing ? enclosing.Parameters.FirstOrDefault(p => p.Name == quoted[0]) : null : null);
                    if (parameterSymbol?.ContainingSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation) is MethodDeclarationSyntax { Body: { } body })
                    {
                        var marker = "Sdsl.Undefined(out " + parameterSymbol.Name + ");";
                        if (!body.ToString().Contains(marker))
                            change = new TextChange(new TextSpan(body.OpenBraceToken.Span.End, 0), " " + marker);
                    }
                    break;
                }

                // A property (a swizzle, a stream) passed to a ref parameter: through a slot C# can pass.
                case "CS0206":
                {
                    var argument = node as ArgumentSyntax ?? node.FirstAncestorOrSelf<ArgumentSyntax>();
                    if (argument != null && !argument.RefKindKeyword.IsKind(SyntaxKind.None))
                        change = new TextChange(argument.Expression.Span, "Sdsl.Ref(" + argument.Expression + ")");
                    break;
                }

                // A dynamic argument in a call through base: through the class itself, which C# can dispatch.
                case "CS1971":
                    if (node.FirstAncestorOrSelf<InvocationExpressionSyntax>()?.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax baseExpression })
                        change = new TextChange(baseExpression.Span, "Sdsl.Base(this)");
                    break;

                // A local named like a function it then calls (float3 min = min(a, b)): rename the local.
                case "CS0149":
                    if (node is IdentifierNameSyntax called)
                    {
                        foreach (var candidate in model.LookupSymbols(called.SpanStart, name: called.Identifier.ValueText))
                            if (candidate is ILocalSymbol shadowingLocal && !renames.ContainsKey(shadowingLocal))
                                renames[shadowingLocal] = shadowingLocal.Name + "_" + (renames.Count + 1).ToString(CultureInfo.InvariantCulture);
                    }
                    break;

                // uint u = -1: HLSL wraps it.
                case "CS0031":
                    if (quoted.Count >= 2 && Expression(node, span) is { } constant)
                        change = new TextChange(constant.Span, "Sdsl.Implicit<" + TypeName(quoted[1]) + ">(" + constant + ")");
                    break;

                // An in parameter written to: HLSL allows it, C#'s in is read-only. The attribute keeps the keyword.
                case "CS8331":
                case "CS8332":
                {
                    // A write to an in parameter binds to no symbol, only to a candidate.
                    var writtenInfo = model.GetSymbolInfo(node, cancellation);
                    if ((writtenInfo.Symbol ?? writtenInfo.CandidateSymbols.FirstOrDefault()) is IParameterSymbol { RefKind: RefKind.In } written
                        && written.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation) is ParameterSyntax writtenSyntax)
                    {
                        var inKeyword = writtenSyntax.Modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.InKeyword));
                        if (inKeyword != default)
                            change = new TextChange(inKeyword.Span, "[Modifiers(\"in\")]");
                    }
                    break;
                }

                // Use of possibly unassigned field 'z' (of a vector local written component by component): the local gets = default.
                case "CS0170":
                {
                    var fieldAccess = node as MemberAccessExpressionSyntax ?? node.FirstAncestorOrSelf<MemberAccessExpressionSyntax>();
                    if (fieldAccess != null && model.GetSymbolInfo(fieldAccess.Expression, cancellation).Symbol is ILocalSymbol partial
                        && partial.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation) is VariableDeclaratorSyntax partialDeclarator
                        && partialDeclarator.Initializer == null)
                        change = new TextChange(new TextSpan(partialDeclarator.Identifier.Span.End, 0), " = default");
                    break;
                }

                // Use of unassigned local variable 'x'
                case "CS0165":
                    if (quoted.Count >= 1 && model.GetSymbolInfo(node, cancellation).Symbol is ILocalSymbol local
                        && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation) is VariableDeclaratorSyntax declarator
                        && declarator.Initializer == null)
                        change = new TextChange(new TextSpan(declarator.Identifier.Span.End, 0), " = default");
                    break;

                // An object reference is required for the non-static member 'X.M'
                case "CS0120":
                    if (node is MemberAccessExpressionSyntax access && model.GetSymbolInfo(access.Expression, cancellation).Symbol is INamedTypeSymbol type)
                        change = new TextChange(access.Expression.Span, "Sdsl.Static<" + access.Expression + ">()");
                    else if (node.Parent is MemberAccessExpressionSyntax parentAccess && model.GetSymbolInfo(parentAccess.Expression, cancellation).Symbol is INamedTypeSymbol)
                        change = new TextChange(parentAccess.Expression.Span, "Sdsl.Static<" + parentAccess.Expression + ">()");
                    break;

                // The name 'X' does not exist in the current context: a macro, when it looks like one.
                case "CS0103":
                    if (node is IdentifierNameSyntax name && MacroLike.IsMatch(name.Identifier.ValueText) && !(name.Parent is MemberAccessExpressionSyntax m && m.Name == name))
                        change = new TextChange(name.Span, "Sdsl.Macro(\"" + name.Identifier.ValueText + "\")");
                    break;

                // Operator '!' cannot be applied to operand of type 'int'
                case "CS0023":
                    if (node is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not && quoted.Count >= 2 && IsNumeric(quoted[1]))
                        change = new TextChange(not.Operand.Span, "Sdsl.Implicit<bool>(" + not.Operand + ")");
                    break;

                // Operator 'op' cannot be applied to operands of type 'A' and 'B'
                case "CS0019":
                    if (node is BinaryExpressionSyntax binary && quoted.Count >= 3)
                        change = FixBinary(binary, quoted[1], quoted[2]);
                    break;

                // Type of conditional expression cannot be determined: no conversion between 'A' and 'B'
                case "CS0173":
                    if (node is ConditionalExpressionSyntax conditional && quoted.Count >= 2)
                    {
                        var wider = Wider(quoted[0], quoted[1]);
                        if (wider == TypeName(quoted[0]))
                            change = new TextChange(conditional.WhenFalse.Span, "Sdsl.Implicit<" + wider + ">(" + conditional.WhenFalse + ")");
                        else if (wider == TypeName(quoted[1]))
                            change = new TextChange(conditional.WhenTrue.Span, "Sdsl.Implicit<" + wider + ">(" + conditional.WhenTrue + ")");
                    }
                    break;

                // Control cannot fall out of switch from the final case label
                case "CS8070":
                case "CS0163":
                {
                    var section = node.FirstAncestorOrSelf<SwitchSectionSyntax>();
                    if (section != null && section.Statements.Count > 0)
                        change = new TextChange(new TextSpan(section.Statements.Last().Span.End, 0), " break;");
                    break;
                }

                // A local named like one of an enclosing scope: HLSL allows it, C# does not. Rename the inner one.
                case "CS0136":
                    if (node is VariableDeclaratorSyntax shadowing && model.GetDeclaredSymbol(shadowing, cancellation) is ILocalSymbol shadow && !renames.ContainsKey(shadow))
                        renames[shadow] = shadow.Name + "_" + (renames.Count + 1).ToString(CultureInfo.InvariantCulture);
                    else if (node is ForEachStatementSyntax shadowingForeach && model.GetDeclaredSymbol(shadowingForeach, cancellation) is ILocalSymbol shadowForeach && !renames.ContainsKey(shadowForeach))
                        renames[shadowForeach] = shadowForeach.Name + "_" + (renames.Count + 1).ToString(CultureInfo.InvariantCulture);
                    break;

                // A constant initializer that is not a C# constant: keep the SDSL const in a marker.
                case "CS0133":
                    if (node.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is { } constLocal && constLocal.IsConst)
                    {
                        var text = constLocal.ToString();
                        var declaration = constLocal.Declaration;
                        var rewritten = declaration.Type + " " + string.Join(", ", declaration.Variables.Select(v =>
                            v.Identifier.Text + (v.Initializer == null ? string.Empty : " = Sdsl.Const(" + v.Initializer.Value + ")"))) + ";";
                        change = new TextChange(constLocal.Span, rewritten);
                    }
                    else if (node.FirstAncestorOrSelf<FieldDeclarationSyntax>() is { } constField && constField.Modifiers.Any(SyntaxKind.ConstKeyword))
                    {
                        var constKeyword = constField.Modifiers.First(m => m.IsKind(SyntaxKind.ConstKeyword));
                        change = new TextChange(constKeyword.Span, "static readonly");
                    }
                    break;
            }

            if (change is { } c && !changes.Any(existing => existing.Span.IntersectsWith(c.Span) || existing.Span.Contains(c.Span.Start) && c.Span.Length == 0 && existing.Span.Length == 0 && existing.Span.Start == c.Span.Start))
                changes.Add(c);
        }

        // Renames: the declaration and every reference.
        foreach (var pair in renames)
        {
            foreach (var reference in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                // min(a, b) next to a local named min: the call means the function, not the local.
                if (reference.Parent is InvocationExpressionSyntax call && call.Expression == reference)
                    continue;
                if (reference.Identifier.ValueText == pair.Key.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference, cancellation).Symbol, pair.Key))
                    AddIfFree(changes, new TextChange(reference.Identifier.Span, pair.Value));
            }
            foreach (var syntax in pair.Key.DeclaringSyntaxReferences)
            {
                var declared = syntax.GetSyntax(cancellation);
                var identifier = declared switch
                {
                    VariableDeclaratorSyntax v => v.Identifier,
                    ForEachStatementSyntax f => f.Identifier,
                    _ => default,
                };
                if (identifier != default)
                    AddIfFree(changes, new TextChange(identifier.Span, pair.Value));
            }
        }

        applied = changes.Count;
        if (applied == 0)
            return tree;
        var text2 = tree.GetText(cancellation).WithChanges(changes);
        return tree.WithChangedText(text2);
    }

    private static void AddIfFree(List<TextChange> changes, TextChange change)
    {
        if (!changes.Any(existing => existing.Span.IntersectsWith(change.Span)))
            changes.Add(change);
    }

    /// <summary>The expression a diagnostic span covers.</summary>
    private static ExpressionSyntax? Expression(SyntaxNode node, TextSpan span)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current is ExpressionSyntax expression && current.Span == span)
                return expression;
            if (current.Span.Length > span.Length)
                break;
        }
        return node as ExpressionSyntax ?? (node as ArgumentSyntax)?.Expression;
    }

    /// <summary>
    /// The conversion HLSL does implicitly, marked so C# accepts it and the SDSL stays as written:
    /// <c>Sdsl.Implicit&lt;float3&gt;(v)</c> translates back to <c>v</c>.
    /// </summary>
    private static TextChange? Convert(ExpressionSyntax expression, string from, string to, SemanticModel model, CancellationToken cancellation)
    {
        to = TypeName(to);
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NumericLiteralExpression) && to == "float" && literal.Token.Value is double)
            return new TextChange(expression.Span, literal.Token.Text.TrimEnd('d', 'D', 'm', 'M') + "f");
        return new TextChange(expression.Span, "Sdsl.Implicit<" + to + ">(" + expression + ")");
    }

    /// <summary>
    /// mul(float4 v, float3x3 m): HLSL truncates the vector to the matrix side it multiplies (its rows
    /// on the left, its columns on the right); C# finds no overload.
    /// </summary>
    private static TextChange? FixMul(InvocationExpressionSyntax call, SemanticModel model, CancellationToken cancellation)
    {
        if (call.Expression is not IdentifierNameSyntax { Identifier.ValueText: "mul" } || call.ArgumentList.Arguments.Count != 2)
            return null;
        var left = call.ArgumentList.Arguments[0].Expression;
        var right = call.ArgumentList.Arguments[1].Expression;
        var leftType = HlslType.Match(TypeName(model.GetTypeInfo(left, cancellation).Type?.ToDisplayString() ?? string.Empty));
        var rightType = HlslType.Match(TypeName(model.GetTypeInfo(right, cancellation).Type?.ToDisplayString() ?? string.Empty));
        if (!leftType.Success || !rightType.Success)
            return null;
        ExpressionSyntax vector;
        Match vectorType;
        int size;
        if (leftType.Groups[2].Success && !leftType.Groups[3].Success && rightType.Groups[3].Success)
        {
            (vector, vectorType) = (left, leftType);
            size = int.Parse(rightType.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (rightType.Groups[2].Success && !rightType.Groups[3].Success && leftType.Groups[3].Success)
        {
            (vector, vectorType) = (right, rightType);
            size = int.Parse(leftType.Groups[3].Value.Substring(1), CultureInfo.InvariantCulture);
        }
        else
            return null;
        if (int.Parse(vectorType.Groups[2].Value, CultureInfo.InvariantCulture) <= size)
            return null;
        return new TextChange(vector.Span, "Sdsl.Implicit<" + vectorType.Groups[1].Value + size.ToString(CultureInfo.InvariantCulture) + ">(" + vector + ")");
    }

    private static TextChange? FixBinary(BinaryExpressionSyntax binary, string left, string right)
    {
        left = TypeName(left);
        right = TypeName(right);
        if ((binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression)))
        {
            // int && int: conditions.
            if (IsNumeric(left))
                return new TextChange(binary.Left.Span, "Sdsl.Implicit<bool>(" + binary.Left + ")");
            if (IsNumeric(right))
                return new TextChange(binary.Right.Span, "Sdsl.Implicit<bool>(" + binary.Right + ")");
            return null;
        }
        if (binary.IsKind(SyntaxKind.LeftShiftExpression) || binary.IsKind(SyntaxKind.RightShiftExpression))
            return new TextChange(binary.Right.Span, "Sdsl.Implicit<int>(" + binary.Right + ")");
        var wider = Wider(left, right);
        if (wider == null)
            return null;
        if (wider == left)
            return new TextChange(binary.Right.Span, "Sdsl.Implicit<" + wider + ">(" + binary.Right + ")");
        if (wider == right)
            return new TextChange(binary.Left.Span, "Sdsl.Implicit<" + wider + ">(" + binary.Left + ")");
        // int3 * float: float3, which neither side is. The vector side converts; the scalar then fits.
        var vectorSide = HlslType.Match(left).Groups[2].Success ? binary.Left : binary.Right;
        return new TextChange(vectorSide.Span, "Sdsl.Implicit<" + wider + ">(" + vectorSide + ")");
    }

    private static readonly Regex HlslType = new Regex(@"^(bool|int|uint|half|float|double|long|ulong)([2-4])?(x[2-4])?$", RegexOptions.Compiled);

    private static int Rank(string scalar) => scalar switch { "bool" => 0, "int" => 1, "long" => 1, "uint" => 2, "ulong" => 2, "half" => 3, "float" => 4, "double" => 5, _ => -1 };

    /// <summary>The type both sides convert to, HLSL's way: the larger vector, then the higher scalar rank.</summary>
    private static string? Wider(string a, string b)
    {
        a = TypeName(a);
        b = TypeName(b);
        var ma = HlslType.Match(a);
        var mb = HlslType.Match(b);
        if (!ma.Success || !mb.Success)
            return null;
        int sizeA = ma.Groups[2].Success ? int.Parse(ma.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
        int sizeB = mb.Groups[2].Success ? int.Parse(mb.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
        var scalar = Rank(ma.Groups[1].Value) >= Rank(mb.Groups[1].Value) ? ma.Groups[1].Value : mb.Groups[1].Value;
        int size = sizeA == 1 ? sizeB : sizeB == 1 ? sizeA : Math.Min(sizeA, sizeB);
        var result = size == 1 ? scalar : scalar + size.ToString(CultureInfo.InvariantCulture);
        return result == a ? a : result == b ? b : result;
    }

    /// <summary>A vector to a smaller vector or a scalar: HLSL truncates implicitly.</summary>
    private static bool Truncates(string from, string to)
    {
        var mf = HlslType.Match(TypeName(from));
        var mt = HlslType.Match(TypeName(to));
        if (!mf.Success || !mt.Success || mf.Groups[3].Success || mt.Groups[3].Success)
            return false;
        int sizeFrom = mf.Groups[2].Success ? int.Parse(mf.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
        int sizeTo = mt.Groups[2].Success ? int.Parse(mt.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
        return sizeTo < sizeFrom;
    }

    /// <summary>The call the argument is in has a single method it can be: the conversion is to that method's parameter.</summary>
    private static bool SingleCandidate(SyntaxNode node, SemanticModel model, CancellationToken cancellation)
    {
        var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation == null)
            return false;
        var info = model.GetSymbolInfo(invocation, cancellation);
        if (info.Symbol != null)
            return true;
        if (info.CandidateSymbols.Length != 1)
            return false;
        // Not an intrinsic: HLSL would pick among its overloads by the argument, not truncate it.
        return info.CandidateSymbols[0].ContainingType?.ToDisplayString() != "Csl.Types.Intrinsics";
    }

    /// <summary>Both types are scalars, or vectors of the same size, or matrices of the same shape.</summary>
    private static bool SameShape(string a, string b)
    {
        var ma = HlslType.Match(TypeName(a));
        var mb = HlslType.Match(TypeName(b));
        return ma.Success && mb.Success && ma.Groups[2].Value == mb.Groups[2].Value && ma.Groups[3].Value == mb.Groups[3].Value;
    }

    private static bool IsNumeric(string type) => type is "int" or "uint" or "float" or "half" or "double" or "long" or "ulong";

    /// <summary>A type name as the compiler printed it, without the Csl namespaces.</summary>
    private static string TypeName(string name)
    {
        foreach (var prefix in new[] { "Csl.Types.", "Csl.", "global::" })
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                name = name.Substring(prefix.Length);
        return name;
    }

    private static string Parenthesize(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax or LiteralExpressionSyntax or InvocationExpressionSyntax or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax or ParenthesizedExpressionSyntax or ObjectCreationExpressionSyntax
            ? expression.ToString()
            : "(" + expression + ")";
}
