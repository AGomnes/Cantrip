#nullable enable
using GameplayEffects.Runtime;
using Xunit;

namespace GameplayEffects.Tests.Battle
{
    /// <summary>
    /// Cooldowns, and the <c>cooldown</c> modifier channel that shortens them.
    /// </summary>
    /// <remarks>
    /// The first test here pins behaviour that shipped long ago and was never covered: nothing
    /// anywhere used an ability twice and checked that the second attempt was refused. Modifiers see
    /// the cooldown already converted into clock units, so a multiplier reads the same on either
    /// clock; the turn-clock test is what keeps that honest.
    /// </remarks>
    public sealed class CooldownTests
    {
        private const string Content = """
            ability "Bolt"
              cooldown 10s
              effect:
                deal 5 to enemies

            ability "Quick"
              cooldown 10s
              modify cooldown: x0.5
              effect:
                deal 1 to enemies

            ability "Patience"
              cooldown 2 turns
              effect:
                deal 1 to enemies

            relic "Vestments"
              modify cooldown: x0.25

            relic "Instant"
              modify cooldown: x0

            enemy "Dummy"
              hp 200
            """;

        private static CardRuntime Setup(out Entity player, IGameClock? clock = null, string? relic = null)
        {
            CardRuntime runtime = BattleKit.Create(Content, clock: clock);
            player = runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            if (relic != null) runtime.AddRelic(relic);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        [Fact]
        public void An_ability_waits_its_cooldown_before_it_can_be_used_again()
        {
            CardRuntime runtime = Setup(out Entity player, new TickClock(10));
            Entity bolt = runtime.GrantAbility("Bolt", player);

            Assert.True(runtime.UseAbility(bolt));
            Assert.False(runtime.UseAbility(bolt));

            runtime.Tick(99);
            Assert.False(runtime.IsReady(bolt));

            runtime.Tick(1);
            Assert.True(runtime.IsReady(bolt));
            Assert.True(runtime.UseAbility(bolt));
        }

        [Fact]
        public void A_modifier_on_the_ability_shortens_that_ability_and_no_other()
        {
            CardRuntime runtime = Setup(out Entity player, new TickClock(10));
            Entity quick = runtime.GrantAbility("Quick", player);
            Entity bolt = runtime.GrantAbility("Bolt", player);

            Assert.True(runtime.UseAbility(quick));
            Assert.True(runtime.UseAbility(bolt));

            runtime.Tick(50);

            // Quick halves its own ten seconds; Bolt, written beside it, waits the full hundred ticks.
            Assert.True(runtime.IsReady(quick));
            Assert.False(runtime.IsReady(bolt));
        }

        [Fact]
        public void A_relic_shortens_every_ability_its_holder_has()
        {
            CardRuntime runtime = Setup(out Entity player, new TickClock(10), relic: "Vestments");
            Entity bolt = runtime.GrantAbility("Bolt", player);

            Assert.True(runtime.UseAbility(bolt));

            runtime.Tick(24);
            Assert.False(runtime.IsReady(bolt));

            runtime.Tick(1);
            Assert.True(runtime.IsReady(bolt));
        }

        [Fact]
        public void A_cooldown_cannot_be_reduced_below_nothing()
        {
            CardRuntime runtime = Setup(out Entity player, new TickClock(10), relic: "Instant");
            Entity bolt = runtime.GrantAbility("Bolt", player);

            Assert.True(runtime.UseAbility(bolt));

            // Nothing to wait for, so it is ready in the same instant rather than going negative.
            Assert.True(runtime.IsReady(bolt));
            Assert.True(runtime.UseAbility(bolt));
        }

        [Fact]
        public void The_same_multiplier_reads_the_same_way_on_a_turn_clock()
        {
            CardRuntime runtime = Setup(out Entity player, clock: null, relic: "Vestments");
            Entity patience = runtime.GrantAbility("Patience", player);

            Assert.True(runtime.UseAbility(patience));
            Assert.False(runtime.IsReady(patience));

            // Two turns quartered rounds up to one, because a duration rounds up when it converts.
            runtime.EndTurn();
            Assert.True(runtime.IsReady(patience));
        }
    }
}
