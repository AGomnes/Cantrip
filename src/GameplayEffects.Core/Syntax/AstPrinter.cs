using System.Linq;
using System.Text;

namespace GameplayEffects.Syntax
{
    /// <summary>Turns expressions back into DSL text, for error messages, test failures and tooling.</summary>
    public static class AstPrinter
    {
        public static string Print(ExprNode node)
        {
            switch (node)
            {
                case NumberExpr number:
                    return number.Value + (number.Unit ?? string.Empty);
                case StringExpr text:
                    return "\"" + text.Value.Replace("\"", "\\\"") + "\"";
                case NameExpr name:
                    return name.Name;
                case QualifiedExpr qualified:
                    return qualified.Qualifier + ":" + qualified.Name;
                case MemberExpr member:
                    return Print(member.Target) + "." + member.Member;
                case CallExpr call:
                {
                    string arguments = string.Join(", ", call.Arguments.Select(Print));
                    return call.Receiver == null
                        ? $"{call.Name}({arguments})"
                        : $"{Print(call.Receiver)}.{call.Name}({arguments})";
                }
                case UnaryExpr unary:
                    return unary.Operator == UnaryOperator.Negate ? "-" + Wrap(unary.Operand) : "not " + Wrap(unary.Operand);
                case BinaryExpr binary:
                    return $"{Wrap(binary.Left)} {Symbol(binary.Operator)} {Wrap(binary.Right)}";
                case RangeExpr range:
                    return Print(range.Low) + ".." + Print(range.High);
                case WhereExpr where:
                    return Print(where.Source) + " where " + Print(where.Predicate);
                case SelectorExpr selector:
                {
                    var text = new StringBuilder(selector.Modifier.ToString().ToLowerInvariant()).Append(' ');
                    if (selector.Count != null) text.Append(Print(selector.Count)).Append(' ');
                    if (selector.Key != null && selector.Key != "hp") text.Append(selector.Key).Append(' ');
                    return text.Append(Print(selector.Source)).ToString();
                }
                default:
                    return node.GetType().Name;
            }
        }

        private static string Wrap(ExprNode node) => node is BinaryExpr ? "(" + Print(node) + ")" : Print(node);

        public static string Symbol(BinaryOperator op) => op switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterOrEqual => ">=",
            BinaryOperator.And => "and",
            BinaryOperator.Or => "or",
            BinaryOperator.Per => "per",
            BinaryOperator.On => "on",
            BinaryOperator.In => "in",
            BinaryOperator.Has => "has",
            _ => op.ToString(),
        };

        public static bool IsComparison(BinaryOperator op) =>
            op == BinaryOperator.Equal || op == BinaryOperator.NotEqual
            || op == BinaryOperator.Less || op == BinaryOperator.LessOrEqual
            || op == BinaryOperator.Greater || op == BinaryOperator.GreaterOrEqual;
    }
}
