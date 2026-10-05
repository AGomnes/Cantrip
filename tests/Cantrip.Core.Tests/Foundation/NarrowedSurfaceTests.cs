#nullable enable
using System;
using System.Reflection;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Foundation
{
    /// <summary>
    /// Small pieces of the surface that each let a caller break the engine quietly: a modifier
    /// evaluator that could be unset, a generator state frozen at four words, a causal chain anyone
    /// could mint, a chooser that swallowed a null, and a conversion that wrapped round.
    /// </summary>
    public sealed class NarrowedSurfaceTests
    {
        // The modifier pipeline ------------------------------------------------------------------

        /// <summary>
        /// <c>IModifierEvaluator</c> is internal, and so is the property that holds one. It was public
        /// and unimplementable: <c>Interpreter</c> is the only implementer, implements both members
        /// explicitly, and the setter had already been made internal because a null there stopped
        /// every modifier applying in silence. Left public at 1.0 it would have carried an interface's
        /// whole freeze cost, a member added in 1.x breaks every implementation, for a surface
        /// nobody outside the library can implement, and it is not one of the four seams
        /// <c>docs/stability.md</c> promises may grow a member with a default.
        /// </summary>
        [Fact]
        public void The_modifier_evaluator_is_not_a_surface_a_game_can_see()
        {
            Assembly core = typeof(ModifierPipeline).Assembly;
            Type? evaluator = core.GetType("Cantrip.Runtime.IModifierEvaluator");

            Assert.NotNull(evaluator);
            Assert.False(evaluator!.IsPublic, "IModifierEvaluator is unimplementable from outside, so it is internal.");
            Assert.DoesNotContain(core.GetExportedTypes(), t => t == evaluator);

            Assert.Null(typeof(ModifierPipeline).GetProperty("Evaluator", BindingFlags.Public | BindingFlags.Instance));
            Assert.NotNull(typeof(ModifierPipeline).GetProperty("Evaluator", BindingFlags.NonPublic | BindingFlags.Instance));
        }

        /// <summary>
        /// One word for the action a value came from, on all three of the types that carry one, and it
        /// is <c>Action</c> rather than <c>Card</c> because two of them have always been able to hold
        /// an ability: the <c>targetable</c> channel sets the query's from whatever is being aimed, and
        /// <c>ModifierContext</c> copies that straight into the context it evaluates in. The word
        /// <c>Action</c> is also the one an ability needs in these three places, and a name cannot be
        /// taken back once the surface is a promise.
        /// </summary>
        [Theory]
        [InlineData(typeof(ModifierQuery))]
        [InlineData(typeof(EvalContext))]
        [InlineData(typeof(GameEvent))]
        public void The_action_a_value_came_from_is_called_Action(Type carrier)
        {
            Assert.Null(carrier.GetProperty("Card"));

            PropertyInfo? action = carrier.GetProperty("Action");
            Assert.NotNull(action);
            Assert.Equal(typeof(Entity), action!.PropertyType);
            Assert.True(action.GetMethod!.IsPublic && action.SetMethod!.IsPublic);
        }

        /// <summary>
        /// And the reason it had to move: a <c>targetable</c> rule written with <c>card:</c> matches an
        /// <em>ability</em> being aimed, because the query carries whichever of the two is in hand.
        /// The DSL word stays <c>card:</c>, frozen content vocabulary, and right in the case content
        /// overwhelmingly writes, while the C# member says what it really holds.
        /// </summary>
        [Fact]
        public void The_query_carries_an_ability_which_is_why_the_member_is_not_called_Card()
        {
            CardRuntime runtime = CardRuntime.FromText("""
                ability "Smite"
                  target enemy
                  effect:
                    deal 5 to target

                status "Warded"
                  stacking none
                  modify targetable where card:Smite: set 0

                enemy "Dummy"
                  hp 40
                """);
            Entity player = runtime.CreatePlayer();
            Entity enemy = runtime.SpawnEnemy("Dummy");
            Entity smite = runtime.GrantAbility("Smite", player);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(new[] { enemy }, runtime.LegalTargets(smite));

            runtime.ApplyStatus("Warded", enemy);

            // `card:Smite` matched an ability, so the query's member was never only a card.
            Assert.Empty(runtime.LegalTargets(smite));
            Assert.Equal(ActionResult.InvalidTarget, runtime.UseAbility(smite));
        }

        [Theory]
        [InlineData("Applies")]
        [InlineData("Amount")]
        public void The_interpreter_evaluates_modifiers_only_through_the_interface(string method)
        {
            Assert.Null(typeof(Interpreter).GetMethod(method, BindingFlags.Public | BindingFlags.Instance));

            // Still implemented, explicitly, which is how the pipeline calls it.
            Assert.Contains(typeof(IModifierEvaluator).GetMethods(), m => m.Name == method);
        }

        [Fact]
        public void The_evaluator_is_in_place_and_modifiers_apply()
        {
            CardRuntime runtime = CardRuntime.FromText("""
                relic "Gauntlet"
                  modify damage: +2

                card "Strike"
                  cost 0
                  target enemy
                  effect:
                    deal 6 to target

                enemy "Dummy"
                  hp 40
                """);
            runtime.CreatePlayer();
            Entity enemy = runtime.SpawnEnemy("Dummy");
            runtime.AddRelic("Gauntlet");
            runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.NotNull(runtime.State.Modifiers.Evaluator);
            Assert.Equal(ActionResult.Played, runtime.Play("Strike", enemy));
            Assert.Equal(32, enemy.GetInt("hp"));
        }

        // The generator --------------------------------------------------------------------------

        [Fact]
        public void The_generator_state_is_a_list_of_words_that_can_grow()
        {
            var rng = new Rng(7);
            ulong[] state = rng.GetState();

            Assert.Equal(Rng.StateWords, state.Length);

            ulong first = rng.NextUInt64();
            ulong second = rng.NextUInt64();

            rng.SetState(state);
            Assert.Equal(first, rng.NextUInt64());
            Assert.Equal(second, rng.NextUInt64());
        }

        [Fact]
        public void A_restored_generator_names_no_seed_for_a_host_to_mistake_for_a_save()
        {
            // Reflection rather than `Assert.Null(rng.Seed)` so that this test says what it means
            // against a build where Seed is a plain ulong: the type is half the fix.
            PropertyInfo seed = typeof(Rng).GetProperty("Seed")!;
            Assert.Equal(typeof(ulong?), seed.PropertyType);

            var rng = new Rng(7);
            Assert.Equal((object)7UL, seed.GetValue(rng));

            rng.NextUInt64();
            rng.SetState(new Rng(99).GetState());

            // The seed named where it started, and it is no longer there. A host that saved this
            // and called `new Rng(saved)` would have rewound the run to its first number.
            Assert.Null(seed.GetValue(rng));
            Assert.Null(seed.GetValue(new Rng(1, 2, 3, 4)));

            // Drawing numbers does not clear it: it is still the label the run started from.
            var fresh = new Rng(7);
            fresh.NextUInt64();
            Assert.Equal((object)7UL, seed.GetValue(fresh));
        }

        [Fact]
        public void A_state_of_the_wrong_size_is_refused_rather_than_half_restored()
        {
            var rng = new Rng(7);

            Assert.Throws<ArgumentException>(() => rng.SetState(new ulong[] { 1, 2, 3 }));
            Assert.Throws<ArgumentNullException>(() => rng.SetState(null!));
        }

        // Settings nothing reads ------------------------------------------------------------------

        [Fact]
        public void No_pacing_setting_is_frozen_for_a_feature_that_does_not_exist()
        {
            // ExecutionMode was carried on RuntimeOptions and CardRuntime and read by nothing. A
            // 1.x that honoured it would change what every game that had set it did, so "keep it
            // and honour it later" was never the additive move it looked like. It went before 1.0.
            Assert.Null(typeof(CardRuntime).Assembly.GetType("Cantrip.ExecutionMode"));
            Assert.Null(typeof(CardRuntime).GetProperty("Execution"));
            Assert.Null(typeof(RuntimeOptions).GetProperty("Execution"));
        }

        // The causal chain -----------------------------------------------------------------------

        [Fact]
        public void A_chain_root_cannot_be_minted_by_game_code()
        {
            Assert.Null(typeof(Chain).GetMethod("NewRoot", BindingFlags.Public | BindingFlags.Static));
            Assert.NotNull(typeof(Chain).GetMethod("NewRoot", BindingFlags.NonPublic | BindingFlags.Static));
        }

        // The chooser ----------------------------------------------------------------------------

        [Fact]
        public void A_null_chooser_is_refused_rather_than_replaced()
        {
            CardRuntime runtime = CardRuntime.FromText("card \"Strike\"\n  cost 0\n  effect:\n    block 1\n");
            var mine = new ScriptedChooser();
            runtime.Chooser = mine;

            Assert.Throws<ArgumentNullException>(() => runtime.Chooser = null!);
            Assert.Throws<ArgumentNullException>(() => runtime.Interpreter.Chooser = null!);

            // And the one that was there is still there, rather than a default put in its place.
            Assert.Same(mine, runtime.Chooser);
        }

        // Whole numbers --------------------------------------------------------------------------

        [Fact]
        public void A_whole_number_too_large_to_hold_is_refused_rather_than_wrapped()
        {
            // FromInt(long.MaxValue).ToInt() used to be -1.
            Assert.Throws<ArgumentOutOfRangeException>(() => Num.FromInt(long.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => Num.FromInt(long.MinValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => Num.FromInt(10_000_000_000_000L));
        }

        [Fact]
        public void Every_int_still_converts_and_comes_back()
        {
            foreach (int value in new[] { 0, 1, -1, 1000, int.MaxValue, int.MinValue })
            {
                Assert.Equal(value, Num.FromInt(value).ToInt());
                Assert.Equal(value, ((Num)value).ToInt());
            }
        }
    }
}
