using Csl.Generators.Sdsl.Syntax;

namespace Csl.TestApp;

/// <summary>Every expression of a statement tree, depth first.</summary>
internal static class SdslWalker
{
    public static IEnumerable<SdslExpression> Expressions(SdslStatement statement)
    {
        switch (statement)
        {
            case SdslBlock block:
                foreach (var inner in block.Statements)
                    foreach (var e in Expressions(inner)) yield return e;
                break;
            case SdslDeclarationStatement declaration:
                foreach (var declarator in declaration.Declarators)
                    if (declarator.Initializer != null)
                        foreach (var e in Expressions(declarator.Initializer)) yield return e;
                break;
            case SdslExpressionStatement expression:
                foreach (var e in Expressions(expression.Expression)) yield return e;
                break;
            case SdslIfStatement ifStatement:
                foreach (var e in Expressions(ifStatement.Condition)) yield return e;
                foreach (var e in Expressions(ifStatement.Then)) yield return e;
                if (ifStatement.Else != null)
                    foreach (var e in Expressions(ifStatement.Else)) yield return e;
                break;
            case SdslForStatement forStatement:
                if (forStatement.Initializer != null)
                    foreach (var e in Expressions(forStatement.Initializer)) yield return e;
                if (forStatement.Condition != null)
                    foreach (var e in Expressions(forStatement.Condition)) yield return e;
                foreach (var incrementor in forStatement.Incrementors)
                    foreach (var e in Expressions(incrementor)) yield return e;
                foreach (var e in Expressions(forStatement.Body)) yield return e;
                break;
            case SdslForeachStatement foreachStatement:
                foreach (var e in Expressions(foreachStatement.Collection)) yield return e;
                foreach (var e in Expressions(foreachStatement.Body)) yield return e;
                break;
            case SdslWhileStatement whileStatement:
                foreach (var e in Expressions(whileStatement.Condition)) yield return e;
                foreach (var e in Expressions(whileStatement.Body)) yield return e;
                break;
            case SdslDoStatement doStatement:
                foreach (var e in Expressions(doStatement.Body)) yield return e;
                foreach (var e in Expressions(doStatement.Condition)) yield return e;
                break;
            case SdslSwitchStatement switchStatement:
                foreach (var e in Expressions(switchStatement.Expression)) yield return e;
                foreach (var section in switchStatement.Sections)
                    foreach (var inner in section.Statements)
                        foreach (var e in Expressions(inner)) yield return e;
                break;
            case SdslReturnStatement returnStatement when returnStatement.Value != null:
                foreach (var e in Expressions(returnStatement.Value)) yield return e;
                break;
            case SdslConditionalStatement conditional:
                foreach (var branch in conditional.Branches)
                    foreach (var inner in branch.Statements)
                        foreach (var e in Expressions(inner)) yield return e;
                break;
        }
    }

    public static IEnumerable<SdslExpression> Expressions(SdslExpression expression)
    {
        yield return expression;
        IEnumerable<SdslExpression> children = expression switch
        {
            SdslMemberAccess access => new[] { access.Target },
            SdslIndexer indexer => new[] { indexer.Target, indexer.Index },
            SdslCall call => new[] { call.Target }.Concat(call.Arguments),
            SdslCast cast => new[] { cast.Operand },
            SdslUnary unary => new[] { unary.Operand },
            SdslBinary binary => new[] { binary.Left, binary.Right },
            SdslAssignment assignment => new[] { assignment.Target, assignment.Value },
            SdslConditional conditional => new[] { conditional.Condition, conditional.WhenTrue, conditional.WhenFalse },
            SdslInitializerList list => list.Items,
            SdslSequence sequence => sequence.Items,
            _ => Array.Empty<SdslExpression>(),
        };
        foreach (var child in children)
            foreach (var e in Expressions(child))
                yield return e;
    }
}
