using System.Linq;
using System.Text;

namespace Cantrip.Syntax
{
    /// <summary>Turns expressions back into DSL text, for error messages, test failures and tooling.</summary>
    public static class AstPrinter
    {
        public static string Print(ExprNode node) => Print(node, spacedUnits: true);

        /// <summary>
        /// The spelling the effect hash has always been computed from, which runs a word unit into
        /// its number (<c>2turns</c>). Kept apart from <see cref="Print(ExprNode)"/> so that a
        /// <c>text_checked</c> hash recorded by an earlier version still matches.
        /// </summary>
        internal static string PrintCompact(ExprNode node) => Print(node, spacedUnits: false);

        /// <summary>
        /// <c>card:Strike</c>, or <c>card:"Fire Bolt"</c> when the name is not one word and has to be
        /// quoted to be read back.
        /// </summary>
        internal static string Qualified(string qualifier, string name) => qualifier + ":" + Name(name);

        /// <summary>A name as content writes it: bare when it is one word, else in quotes.</summary>
        internal static string Name(string name) => IsWord(name) ? name : Quote(name);

        private static string Print(ExprNode node, bool spacedUnits)
        {
            switch (node)
            {
                case NumberExpr number:
                    return number.Value + Unit(number.Unit, spacedUnits);
                case StringExpr text:
                    return Quote(text.Value);
                case NameExpr name:
                    return name.Name;
                case QualifiedExpr qualified:
                    return Qualified(qualified.Qualifier, qualified.Name);
                case MemberExpr member:
                    return Print(member.Target, spacedUnits) + "." + member.Member;
                case CallExpr call:
                {
                    string arguments = string.Join(", ", call.Arguments.Select(a => Print(a, spacedUnits)));
                    return call.Receiver == null
                        ? $"{call.Name}({arguments})"
                        : $"{Print(call.Receiver, spacedUnits)}.{call.Name}({arguments})";
                }
                case UnaryExpr unary:
                    return unary.Operator == UnaryOperator.Negate ? "-" + Wrap(unary.Operand, spacedUnits) : "not " + Wrap(unary.Operand, spacedUnits);
                case BinaryExpr binary:
                    return $"{Wrap(binary.Left, spacedUnits)} {Symbol(binary.Operator)} {Wrap(binary.Right, spacedUnits)}";
                case RangeExpr range:
                    return Print(range.Low, spacedUnits) + ".." + Print(range.High, spacedUnits);
                case WhereExpr where:
                    return Print(where.Source, spacedUnits) + " where " + Print(where.Predicate, spacedUnits);
                case SelectorExpr selector:
                {
                    var text = new StringBuilder(selector.Modifier.ToString().ToLowerInvariant()).Append(' ');
                    if (selector.Count != null) text.Append(Print(selector.Count, spacedUnits)).Append(' ');
                    if (selector.Key != null && selector.Key != "hp") text.Append(selector.Key).Append(' ');
                    return text.Append(Print(selector.Source, spacedUnits)).ToString();
                }
                default:
                    return node.GetType().Name;
            }
        }

        /// <summary>
        /// A unit that is a word reads as it is written, after a space (<c>2 turns</c>,
        /// <c>3 seconds</c>, <c>3 sec</c>); a symbol or a one- or two-letter unit stays against its
        /// number (<c>40%</c>, <c>3s</c>, <c>250ms</c>). Only words the parser reads after a space
        /// are spaced, so the text still parses back to the same number.
        /// </summary>
        private static string Unit(string? unit, bool spaced)
        {
            if (string.IsNullOrEmpty(unit)) return string.Empty;
            return spaced && unit!.Length > 2 && Parser.UnitWords.Contains(unit) ? " " + unit : unit!;
        }

        private static string Wrap(ExprNode node, bool spacedUnits) => node is BinaryExpr ? "(" + Print(node, spacedUnits) + ")" : Print(node, spacedUnits);

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static bool IsWord(string name) =>
            name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

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
