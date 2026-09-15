using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Review.ReviewSupport;

namespace GameplayEffects.Tests.Review
{
    /// <summary>
    /// Event dispatch: listener limits, before-phase amounts and the trigger queue's lifetime.
    /// Regression tests: each test's summary records a defect a review found, now fixed.
    /// </summary>
    public sealed class ReviewEventTests
    {
        /// <summary>
        /// Decay, resource resets, expiry and until-reverts run with SystemContext, whose chain root
        /// is always 0. Every one of them therefore shares one "chain", so a <c>once per chain</c>
        /// listener fires for the first such event ever and never again.
        /// </summary>
        [Fact]
        [Trait("Regression", "system-context-shares-chain-root")]
        public void Once_per_chain_fires_again_for_a_later_turns_decay()
        {
            CardRuntime runtime = NewRuntime(@"
status ""Timed""
  stacking duration

relic ""Watcher""
  on duration_changed once per chain:
    gain 1 gold
");
            Entity player = runtime.Player!;
            runtime.AddRelic("Watcher");
            runtime.ApplyStatus("Timed", player, 5);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.EndTurn();
            Assert.Equal(1, player.GetInt("gold"));

            runtime.EndTurn();
            Assert.Equal(2, player.GetInt("gold"));
        }

        /// <summary>
        /// <c>once per turn</c> uses State.Turn as its window, and Turn restarts at 1 every battle,
        /// so a relic that fired on turn 1 of one battle is locked out of turn 1 of the next.
        /// </summary>
        [Fact]
        [Trait("Regression", "once-per-turn-window-repeats-across-battles")]
        public void Once_per_turn_resets_in_the_next_battle()
        {
            CardRuntime runtime = NewRuntime(@"
card ""Jab""
  cost 0
  target enemy
  effect:
    deal 100 to target

relic ""Tally""
  on card_played once per turn:
    gain 1 gold
");
            Entity player = runtime.Player!;
            runtime.AddRelic("Tally");

            Entity first = Enemy(runtime);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Jab", Zones.Hand), first));
            Assert.True(runtime.Won == true);
            Assert.Equal(1, player.GetInt("gold"));

            Entity second = Enemy(runtime);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Jab", Zones.Hand), second));

            Assert.Equal(2, player.GetInt("gold"));
        }

        /// <summary>
        /// Before listeners "may change event.amount", but ChangeStat applies the amount it computed
        /// before raising <c>&lt;stat&gt;_changed</c> and ignores the event afterwards.
        /// </summary>
        [Fact]
        [Trait("Regression", "stat-changed-before-amount-ignored")]
        public void Before_stat_changed_listener_can_change_the_amount()
        {
            CardRuntime runtime = NewRuntime(@"
relic ""Greed""
  on before_gold_changed:
    event.amount = event.amount * 2
");
            runtime.AddRelic("Greed");

            runtime.Execute("gain 5 gold");

            Assert.Equal(10, runtime.Player!.GetInt("gold"));
        }

        /// <summary>
        /// Drain drops the queue when a trigger throws, but a top-level action that throws before
        /// reaching Drain leaves its queued triggers behind; the next unrelated action runs them.
        /// </summary>
        [Fact]
        [Trait("Regression", "failed-action-leaves-queued-triggers")]
        public void A_failed_action_does_not_leak_triggers_into_the_next_one()
        {
            CardRuntime runtime = NewRuntime(@"
relic ""Tracker""
  on damaged:
    gain 1 gold
");
            runtime.AddRelic("Tracker");
            Enemy(runtime);

            Assert.Throws<RuntimeError>(() => runtime.Execute("deal 1 to enemy\nexplode"));
            Assert.Equal(0, runtime.Interpreter.PendingTriggers);

            runtime.Execute("log \"unrelated\"");
            Assert.Equal(0, runtime.Player!.GetInt("gold"));
        }

        /// <summary>
        /// EndBattle reverts <c>until</c> blocks after its own Run has drained, so the
        /// status_removed triggers those reverts raise stay queued and fire in the next battle.
        /// </summary>
        [Fact]
        [Trait("Regression", "battle-end-strands-revert-triggers")]
        public void Triggers_raised_while_ending_a_battle_resolve_before_it_returns()
        {
            CardRuntime runtime = NewRuntime(@"
status ""Rage""
  stacking intensity

card ""Finisher""
  cost 0
  target enemy
  effect:
    until turn_end:
      gain 1 Rage
    deal 100 to target

relic ""Mourner""
  on status_removed:
    gain 1 gold
");
            Entity player = runtime.Player!;
            runtime.AddRelic("Mourner");
            Entity enemy = Enemy(runtime, 10);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Finisher", Zones.Hand), enemy));
            Assert.True(runtime.Won == true);
            Assert.Equal(0, player.StacksOf("Rage"));

            Assert.Equal(0, runtime.Interpreter.PendingTriggers);
            Assert.Equal(1, player.GetInt("gold"));
        }
    }
}
