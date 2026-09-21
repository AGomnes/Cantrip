using System.Collections.Generic;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Syntax;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Shared setup for runtime tests. Every test builds its own runtime from inline content, so
    /// the rule under test is visible next to its assertions instead of hidden in a sample file.
    /// </summary>
    internal static class RuntimeTestKit
    {
        /// <summary>Loads content (throwing on errors) and creates the default 80 hp, 3 energy player.</summary>
        public static CardRuntime Create(string content, RuntimeOptions? options = null)
        {
            CardRuntime runtime = CardRuntime.FromText(content, options);
            runtime.CreatePlayer();
            return runtime;
        }

        /// <summary>
        /// A definition-less enemy, like the test runner's <c>enemy hp N</c>: no moves, so ending
        /// the turn never hurts the player and only the rule under test changes state.
        /// </summary>
        public static Entity Enemy(CardRuntime runtime, int hp = 50, string name = "Enemy")
        {
            Entity enemy = runtime.State.Spawn(name, EntityKind.Actor, null, Team.Enemy, Zones.Board);
            enemy.SetBase("max_hp", hp);
            enemy.SetBase("hp", hp);
            enemy.SetBase("block", 0);
            return enemy;
        }

        /// <summary>Starts a battle the way DSL tests do: no shuffle and no opening hand.</summary>
        public static void Start(CardRuntime runtime) => runtime.StartBattle(shuffle: false, drawOpeningHand: false);

        /// <summary>Everything the <c>log</c> verb writes, in order.</summary>
        public static List<string> CaptureLog(CardRuntime runtime)
        {
            var lines = new List<string>();
            runtime.Interpreter.Logged += lines.Add;
            return lines;
        }

        /// <summary>Evaluates one DSL expression from the player's point of view.</summary>
        public static Value Eval(CardRuntime runtime, string expression, Entity? target = null, IDictionary<string, Entity>? locals = null)
        {
            var diagnostics = new DiagnosticBag();
            IReadOnlyList<Token> tokens = Lexer.Tokenize(expression, "<expression>", diagnostics);
            ExprNode node = new Parser(tokens, "<expression>", diagnostics).ParseExpression();
            diagnostics.ThrowIfErrors();

            Entity? player = runtime.Player;
            var context = new EvalContext(player) { Source = player, Target = target };
            if (locals != null)
            {
                foreach (KeyValuePair<string, Entity> local in locals) context.SetLocal(local.Key, Value.FromEntity(local.Value));
            }
            return runtime.Interpreter.Evaluate(node, context);
        }

        public static int EvalInt(CardRuntime runtime, string expression, Entity? target = null, IDictionary<string, Entity>? locals = null) =>
            runtime.Interpreter.ToNumber(Eval(runtime, expression, target, locals), SourceSpan.None).ToInt();

        public static int Hp(Entity entity) => entity.GetInt("hp");

        public static int Stacks(Entity host, string status) => host.StacksOf(status);

        /// <summary>The duration counter of the first instance of a status, or 0 when absent.</summary>
        public static int Duration(Entity host, string status) => host.FindAttached(status)?.GetInt("duration") ?? 0;
    }
}
