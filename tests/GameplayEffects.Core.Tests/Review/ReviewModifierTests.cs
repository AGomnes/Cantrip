using GameplayEffects.Runtime;
using Xunit;
using static GameplayEffects.Tests.Review.ReviewSupport;

namespace GameplayEffects.Tests.Review
{
    /// <summary>Modifier pipeline: layering, channel coverage and stat cache invalidation.</summary>
    public sealed class ReviewModifierTests
    {
        private const string CodexContent = @"
card ""Fireball""
  cost 2
  target enemy
  tags attack, fire
  effect:
    deal 1 to target

relic ""Codex""
  modify cost of cards where tag:fire: -1
";

        /// <summary>
        /// CardRuntime.CostOf reads <c>card.Get("cost")</c>, which already runs the <c>cost</c>
        /// channel through ComputeStat, and then runs the same modifiers again through Compute.
        /// </summary>
        [Fact]
        [Trait("Regression", "cost-modifier-applied-twice")]
        public void Cost_modifier_applies_once()
        {
            CardRuntime runtime = NewRuntime(CodexContent);
            runtime.AddRelic("Codex");
            Entity card = runtime.AddCard("Fireball", Zones.Hand);

            Assert.Equal(1, runtime.CostOf(card));
        }

        [Fact]
        [Trait("Regression", "cost-modifier-applied-twice")]
        public void Playing_a_discounted_card_pays_the_discounted_cost()
        {
            CardRuntime runtime = NewRuntime(CodexContent);
            runtime.AddRelic("Codex");
            Entity enemy = Enemy(runtime, 50);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Fireball", Zones.Hand), enemy));

            Assert.Equal(2, runtime.Player!.GetInt("energy"));
        }

        /// <summary>
        /// The stat cache is keyed on GameState.Version, but advancing the turn (and the clock) does
        /// not bump it. A turn in which nothing else changes leaves a stale cached stat behind.
        /// </summary>
        [Fact]
        [Trait("Regression", "stat-cache-ignores-turn-and-clock")]
        public void Stat_modifier_reading_turn_updates_after_end_turn()
        {
            CardRuntime runtime = NewRuntime(@"
relic ""Hourglass""
  modify armor: +turn
");
            Entity player = runtime.Player!;
            runtime.AddRelic("Hourglass");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            Assert.Equal(1, player.GetInt("armor"));

            runtime.EndTurn();

            Assert.Equal(2, runtime.State.Turn);
            Assert.Equal(2, player.GetInt("armor"));
        }

        [Fact]
        [Trait("Regression", "stat-cache-ignores-turn-and-clock")]
        public void Stat_modifier_reading_now_updates_after_ticks()
        {
            CardRuntime runtime = NewRuntime(@"
relic ""Sundial""
  modify armor: +now
", new RuntimeOptions { Clock = new TickClock(10) });
            Entity player = runtime.Player!;
            runtime.AddRelic("Sundial");
            Assert.Equal(0, player.GetInt("armor"));

            runtime.Tick(5);

            Assert.Equal(5, runtime.State.Clock.Now);
            Assert.Equal(5, player.GetInt("armor"));
        }

        /// <summary>
        /// InDefaultScope treats <c>draw</c> as an outgoing channel like damage and block, but
        /// Interpreter.Draw never consults the pipeline, so <c>modify draw</c> is silently dead.
        /// </summary>
        [Fact]
        [Trait("Regression", "draw-modifiers-never-applied")]
        public void Draw_channel_modifier_changes_cards_drawn()
        {
            CardRuntime runtime = NewRuntime(@"
relic ""Satchel""
  modify draw: +1

card ""Pull""
  cost 0
  effect:
    draw 1

card ""Filler""
  cost 1
");
            runtime.AddRelic("Satchel");
            runtime.AddDeck("Filler", "Filler", "Filler");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Pull", Zones.Hand)));

            Assert.Equal(2, HandCount(runtime));
        }
    }
}
