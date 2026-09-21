using System;
using System.Linq;
using Cantrip.Runtime;
using Xunit;
using static Cantrip.Tests.Review.ReviewSupport;

namespace Cantrip.Tests.Review
{
    /// <summary>
    /// CardRuntime turn flow, battle lifecycle, board layout, selectors and verbs. Regression tests:
    /// each test's summary records a defect an adversarial review of the core found, now fixed.
    /// </summary>
    public sealed class ReviewRuntimeFlowTests
    {
        private const string Minimal = "status \"Marker\"\n";

        /// <summary>
        /// Only Play and Tick reset the step counter. Execute, EndTurn, StartBattle and UseAbility
        /// never do, so the sandbox budget is shared by the whole game and eventually every
        /// action fails with "Step limit exceeded".
        /// </summary>
        [Fact]
        [Trait("Regression", "step-budget-never-reset")]
        public void Execute_gets_a_fresh_step_budget_each_call()
        {
            CardRuntime runtime = NewRuntime(@"
ruleset
  max_steps 10
");
            Exception? error = Record.Exception(() =>
            {
                for (int i = 0; i < 20; i++) runtime.Execute("log \"one statement\"");
            });

            Assert.Null(error);
        }

        [Fact]
        [Trait("Regression", "step-budget-never-reset")]
        public void End_turn_gets_a_fresh_step_budget_each_call()
        {
            CardRuntime runtime = NewRuntime(@"
ruleset
  max_steps 10

status ""Ticker""
  on turn_end:
    log ""tick""
");
            runtime.ApplyStatus("Ticker", runtime.Player!);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Exception? error = Record.Exception(() =>
            {
                for (int i = 0; i < 20; i++) runtime.EndTurn();
            });

            Assert.Null(error);
        }

        /// <summary>
        /// CheckBattleOver asks whether any enemy ever existed in the whole game, not in this
        /// battle. A battle started before its enemies spawn waits in battle 1 but is an instant
        /// win in every later battle, because battle 1's dead enemies still count.
        /// </summary>
        [Fact]
        [Trait("Regression", "battle-over-counts-previous-battles")]
        public void A_later_battle_waits_for_its_enemies_like_the_first()
        {
            CardRuntime runtime = NewRuntime(Minimal);

            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            Assert.True(runtime.State.InBattle);
            Enemy(runtime, 5);
            runtime.Execute("deal 100 to enemy");
            Assert.True(runtime.Won == true);

            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.True(runtime.State.InBattle);
            Assert.Null(runtime.Won);
        }

        /// <summary>
        /// SpawnEnemy rolls an intent when a battle is running, but actors made by the DSL's
        /// <c>create</c> never get one, so a split or summon during the player's turn skips its
        /// first enemy turn.
        /// </summary>
        [Fact]
        [Trait("Regression", "created-enemies-have-no-intent")]
        public void An_enemy_created_mid_turn_acts_on_the_next_enemy_turn()
        {
            CardRuntime runtime = NewRuntime(@"
enemy ""Splitter""
  hp 30
  on self.damaged:
    create Slime

enemy ""Slime""
  hp 10
  move ""Spit"":
    deal 5 to player
");
            runtime.SpawnEnemy("Splitter");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("deal 1 to enemy");
            Entity slime = runtime.State.Actors(Team.Enemy).Single(e => e.Name == "Slime");
            Assert.Equal("Spit", slime.Intent);

            runtime.EndTurn();
            Assert.Equal(75, runtime.Player!.GetInt("hp"));
        }

        /// <summary>
        /// Place gives a new board actor the count of live teammates as its position. After a death
        /// that count can equal an existing actor's slot, so two actors share a position.
        /// </summary>
        [Fact]
        [Trait("Regression", "board-positions-collide-after-death")]
        public void Board_positions_stay_unique_after_a_death_and_a_spawn()
        {
            CardRuntime runtime = NewRuntime(Minimal);
            Enemy(runtime, name: "A");
            Entity b = Enemy(runtime, name: "B");
            Enemy(runtime, name: "C");

            runtime.Execute("deal 100 to target", null, b);
            Assert.True(b.IsDead);
            Enemy(runtime, name: "D");

            var positions = runtime.State.Actors(Team.Enemy).Select(e => e.Position).ToList();
            Assert.Equal(positions.Count, positions.Distinct().Count());
        }

        /// <summary>
        /// Zone names evaluate to the live zone list. RemoveMatching iterates that list without a
        /// snapshot while Destroy takes cards out of it, so <c>remove hand</c> throws.
        /// </summary>
        [Fact]
        [Trait("Regression", "remove-iterates-live-zone")]
        public void Remove_hand_destroys_every_card_in_hand()
        {
            CardRuntime runtime = NewRuntime(@"
card ""Junk""
  cost 1
");
            runtime.AddCard("Junk", Zones.Hand);
            runtime.AddCard("Junk", Zones.Hand);

            Exception? error = Record.Exception(() => runtime.Execute("remove hand"));

            Assert.Null(error);
            Assert.Equal(0, HandCount(runtime));
        }

        /// <summary>
        /// VerbKill only looks at the <c>to</c> clause and the context target, so the positional
        /// argument of <c>kill X</c> is ignored and the context target dies instead.
        /// </summary>
        [Fact]
        [Trait("Regression", "kill-ignores-positional-target")]
        public void Kill_kills_the_named_entity()
        {
            CardRuntime runtime = NewRuntime(Minimal);
            Entity weak = Enemy(runtime, 5, "Weakling");
            Entity strong = Enemy(runtime, 50, "Brute");

            runtime.Execute("kill lowest hp enemies", null, strong);

            Assert.True(weak.IsDead);
            Assert.False(strong.IsDead);
        }

        /// <summary>
        /// <c>lowest &lt;group&gt; where ...</c>: the parser sees two identifiers in a row, takes the
        /// group as the sort key and the word <c>where</c> as the group.
        /// </summary>
        [Fact]
        [Trait("Regression", "lowest-group-where-misparsed")]
        public void Lowest_group_with_where_filter_selects_from_the_group()
        {
            CardRuntime runtime = NewRuntime(Minimal);
            Entity weak = Enemy(runtime, 5, "Weakling");
            Entity strong = Enemy(runtime, 10, "Brute");

            Exception? error = Record.Exception(() => runtime.Execute("deal 1 to lowest enemies where hp > 0"));

            Assert.Null(error);
            Assert.Equal(4, weak.GetInt("hp"));
            Assert.Equal(10, strong.GetInt("hp"));
        }

        /// <summary>
        /// A declared resource is also registered as a definition, and ResolveName checks content
        /// definitions before the controller's stats, so reading the resource by name yields the
        /// definition instead of the number.
        /// </summary>
        [Fact]
        [Trait("Regression", "resource-name-shadows-stat")]
        public void A_declared_resource_reads_as_the_controllers_stat()
        {
            CardRuntime runtime = NewRuntime(@"
resource ""mana""
  min 0
  max 10

card ""Mana Bolt""
  cost 0
  target enemy
  effect:
    deal mana to target
");
            runtime.Player!.SetBase("mana", 4);
            Entity enemy = Enemy(runtime, 20);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Exception? error = Record.Exception(() => runtime.Play(runtime.AddCard("Mana Bolt", Zones.Hand), enemy));

            Assert.Null(error);
            Assert.Equal(16, enemy.GetInt("hp"));
        }

        /// <summary>
        /// <c>apply</c> resolves its name with Content.Find, which returns whichever kind was loaded
        /// first. A card and a status sharing a name (Burn, Wound) makes <c>apply Burn</c> fail.
        /// </summary>
        [Fact]
        [Trait("Regression", "apply-resolves-name-across-kinds")]
        public void Apply_finds_the_status_when_a_card_shares_its_name()
        {
            CardRuntime runtime = NewRuntime(@"
card ""Burn""
  cost 0
  tags unplayable

status ""Burn""
  tags debuff
  stacking intensity

card ""Torch""
  cost 0
  target enemy
  effect:
    apply Burn 2 to target
");
            Entity enemy = Enemy(runtime);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Exception? error = Record.Exception(() => runtime.Play(runtime.AddCard("Torch", Zones.Hand), enemy));

            Assert.Null(error);
            Assert.Equal(2, enemy.StacksOf("Burn"));
        }

        /// <summary>Interpreter.Choose tolerates a null answer; the card target prompt dereferences it.</summary>
        [Fact]
        [Trait("Regression", "target-prompt-trusts-chooser")]
        public void Target_prompt_survives_a_null_answer()
        {
            CardRuntime runtime = NewRuntime(@"
card ""Strike""
  cost 1
  target enemy
  effect:
    deal 6 to target
", new RuntimeOptions { Chooser = new ReviewNullChooser() });
            Enemy(runtime);
            Enemy(runtime);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            PlayResult result = PlayResult.NotACard;
            Exception? error = Record.Exception(() => result = runtime.Play(runtime.AddCard("Strike", Zones.Hand)));

            Assert.Null(error);
            Assert.Equal(PlayResult.Played, result);
        }

        /// <summary>Ruleset.MaxHandSize documents "cards beyond this are discarded instead of drawn"; Draw just stops.</summary>
        [Fact]
        [Trait("Regression", "max-hand-size-does-not-discard")]
        public void Draws_beyond_max_hand_size_are_discarded()
        {
            CardRuntime runtime = NewRuntime(@"
ruleset
  max_hand_size 1

card ""Filler""
  cost 1
");
            runtime.AddCard("Filler", Zones.Hand);
            runtime.AddDeck("Filler", "Filler");

            runtime.Execute("draw 2");

            Assert.Equal(1, HandCount(runtime));
            Assert.Equal(2, runtime.State.ZoneOf(runtime.Player, Zones.Discard).Count);
        }

        /// <summary>
        /// ComputeHash claims to cover "all rules state" for desync detection, but it skips
        /// scheduled work, history counters, enemy intents and listener limit windows. Two states
        /// with different futures hash the same.
        /// </summary>
        [Fact]
        [Trait("Regression", "compute-hash-incomplete")]
        public void Pending_scheduled_work_changes_the_state_hash()
        {
            CardRuntime a = NewRuntime(Minimal);
            CardRuntime b = NewRuntime(Minimal);
            a.StartBattle(shuffle: false, drawOpeningHand: false);
            b.StartBattle(shuffle: false, drawOpeningHand: false);
            Assert.Equal(a.State.ComputeHash(), b.State.ComputeHash());

            a.Execute("next turn:\n  gain 5 gold");

            Assert.NotEqual(a.State.ComputeHash(), b.State.ComputeHash());
        }
    }
}
