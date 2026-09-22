using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Csl.Generators.Sdsl.Syntax;

/// <summary>SDSL text back from the tree, for the pieces kept as SDSL in the C# (a member's other #if versions).</summary>
public static class SdslPrinter
{
    public static string Variable(SdslVariable variable)
    {
        var sb = new StringBuilder();
        foreach (var attribute in variable.Attributes)
            sb.Append('[').Append(attribute.Text).Append("] ");
        foreach (var modifier in variable.Modifiers)
            sb.Append(modifier).Append(' ');
        sb.Append(variable.Type).Append(' ').Append(variable.Name);
        foreach (var size in variable.ArraySizes)
            sb.Append('[').Append(size).Append(']');
        if (variable.Semantic != null)
            sb.Append(" : ").Append(variable.Semantic);
        if (variable.Initializer != null)
            sb.Append(" = ").Append(Expression(variable.Initializer));
        if (variable.SamplerState != null)
            sb.Append(" { ").Append(string.Concat(variable.SamplerState.Select(p => p.Key + " = " + p.Value + "; "))).Append('}');
        return sb.Append(';').ToString();
    }

    public static string Expression(SdslExpression expression)
    {
        var text = ExpressionCore(expression);
        return expression.Parenthesized ? "(" + text + ")" : text;
    }

    private static string ExpressionCore(SdslExpression expression) => expression switch
    {
        SdslLiteral literal => literal.Kind == SdslLiteralKind.String ? "\"" + literal.Text + "\"" : literal.Text,
        SdslIdentifier identifier => identifier.Name,
        SdslMemberAccess access => Expression(access.Target) + "." + access.Name,
        SdslIndexer indexer => Expression(indexer.Target) + "[" + Expression(indexer.Index) + "]",
        SdslCall call => Expression(call.Target) + "(" + string.Join(", ", call.Arguments.Select(Expression)) + ")",
        SdslTypeExpression type => type.Type.ToString(),
        SdslCast cast => "(" + cast.Type + string.Concat(cast.ArraySizes.Select(s => "[" + s + "]")) + ")" + Expression(cast.Operand),
        SdslUnary unary => unary.Postfix ? Expression(unary.Operand) + unary.Operator : unary.Operator + Expression(unary.Operand),
        SdslBinary binary => Expression(binary.Left) + " " + binary.Operator + " " + Expression(binary.Right),
        SdslAssignment assignment => Expression(assignment.Target) + " " + assignment.Operator + " " + Expression(assignment.Value),
        SdslConditional conditional => Expression(conditional.Condition) + " ? " + Expression(conditional.WhenTrue) + " : " + Expression(conditional.WhenFalse),
        SdslInitializerList list => "{ " + string.Join(", ", list.Items.Select(Expression)) + " }",
        SdslSequence sequence => string.Join(", ", sequence.Items.Select(Expression)),
        _ => string.Empty,
    };
}
