namespace GameplayEffects.Syntax
{
    /// <summary>
    /// Visits every statement and expression under a node, in source order. Tools such as the
    /// linter override the hooks they care about and call the base method to keep descending.
    /// </summary>
    public abstract class AstWalker
    {
        public virtual void VisitBlock(BlockNode block)
        {
            foreach (StatementNode statement in block.Statements) VisitStatement(statement);
        }

        public virtual void VisitStatement(StatementNode statement)
        {
            switch (statement)
            {
                case CommandNode command:
                    foreach (ExprNode argument in command.Arguments) VisitExpression(argument);
                    foreach (ClauseNode clause in command.Clauses)
                    {
                        if (clause.Value != null) VisitExpression(clause.Value);
                    }
                    break;

                case IfNode branch:
                    VisitExpression(branch.Condition);
                    VisitBlock(branch.Then);
                    if (branch.Else != null) VisitBlock(branch.Else);
                    break;

                case LetNode let:
                    VisitExpression(let.Value);
                    break;

                case RepeatNode repeat:
                    VisitExpression(repeat.Count);
                    VisitBlock(repeat.Body);
                    break;

                case ForEachNode loop:
                    VisitExpression(loop.Source);
                    VisitBlock(loop.Body);
                    break;

                case ChanceNode chance:
                    VisitExpression(chance.Probability);
                    VisitBlock(chance.Body);
                    if (chance.Else != null) VisitBlock(chance.Else);
                    break;

                case ScheduleNode schedule:
                    if (schedule.Delay != null) VisitExpression(schedule.Delay);
                    VisitBlock(schedule.Body);
                    break;

                case AssignNode assign:
                    VisitExpression(assign.Target);
                    VisitExpression(assign.Value);
                    break;

                case LabeledBlockNode labeled:
                    VisitBlock(labeled.Body);
                    break;
            }
        }

        public virtual void VisitExpression(ExprNode expression)
        {
            switch (expression)
            {
                case MemberExpr member:
                    VisitExpression(member.Target);
                    break;

                case CallExpr call:
                    if (call.Receiver != null) VisitExpression(call.Receiver);
                    foreach (ExprNode argument in call.Arguments) VisitExpression(argument);
                    break;

                case UnaryExpr unary:
                    VisitExpression(unary.Operand);
                    break;

                case BinaryExpr binary:
                    VisitExpression(binary.Left);
                    VisitExpression(binary.Right);
                    break;

                case RangeExpr range:
                    VisitExpression(range.Low);
                    VisitExpression(range.High);
                    break;

                case WhereExpr where:
                    VisitExpression(where.Source);
                    VisitExpression(where.Predicate);
                    break;

                case SelectorExpr selector:
                    if (selector.Count != null) VisitExpression(selector.Count);
                    VisitExpression(selector.Source);
                    break;
            }
        }
    }
}
