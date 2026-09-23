using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Sim;
using Xunit;

namespace Cantrip.Tests.Sim
{
    /// <summary>
    /// The meter, and the one thing that can go wrong with it. Every number it prints is the sum of
    /// an amount the engine itself set, so the way to check it is to ask the engine the same
    /// question twice: what the events said happened to an actor's hp, and what its hp did. They
    /// have to agree, and they only agree while a bot's trials are left out.
    /// </summary>
    public sealed class MeterTests
    {
        /// <summary>
        /// Conservation, over content nobody wrote for this test. For every actor, the damage the
        /// meter counted less the healing it counted is the hp that actor actually lost, read from
        /// the engine's own <c>hp</c>. A meter that counted a bot's lookahead would claim an actor
        /// took several times the damage its hp ever moved by, and this is what says so.
        /// </summary>
        [Theory]
        [InlineData("slice", Bots.Cautious)]
        [InlineData("slice", Bots.Patient)]
        [InlineData("slice", Bots.Random)]
        [InlineData("abilities", Bots.Cautious)]
        public void The_meter_and_the_engines_own_hp_agree_about_every_actor(string sample, string bot)
        {
            ScenarioOutcome outcome = PlaySample(sample, bot, runs: 4);

            Assert.Equal(0, outcome.Errors);
            foreach (RunResult run in outcome.Runs)
            {
                IReadOnlyList<string> wrong = run.Ledger!.Disagreements();
                Assert.True(wrong.Count == 0, $"seed {run.Seed}: " + string.Join("; ", wrong));
            }

            // A guard, so this cannot pass over a meter that counted nothing at all.
            Assert.True(outcome.ByBot[0].Meter.TotalTaken > 0);
            Assert.True(outcome.ByBot[0].Meter.TotalDealt > 0);
        }

        /// <summary>
        /// The flag itself. A bot that plays a card inside a trial deals real damage through the
        /// engine and raises every event a real play would, and none of it may be counted; the same
        /// bot outside a trial is counted in full.
        /// </summary>
        [Fact]
        public void A_play_a_bot_only_tried_is_not_counted()
        {
            var tried = new OnePlayBot(inATrial: true);
            var made = new OnePlayBot(inATrial: false);

            Meter afterTrials = Meter(Play(OneHit, tried));
            Meter afterPlays = Meter(Play(OneHit, made));

            // The trial really did play the card: the bot only returns true when the engine took it.
            Assert.True(tried.Played);
            Assert.Equal(0L, afterTrials.TotalDealt);
            Assert.Empty(afterTrials.Cards);
            Assert.Empty(afterTrials.DamageDealt);

            Assert.True(made.Played);
            Assert.Equal(6L, afterPlays.DamageDealt.Single(t => t.Name == "Zap").Amount);
            Assert.Equal(1, afterPlays.Cards.Single(t => t.Name == "Zap").Times);
        }

        /// <summary>
        /// The same claim from the other end: the enemy's move fires in a trial, because a
        /// lookahead ends the turn, and the meter must not have it among the moves that were used.
        /// </summary>
        [Fact]
        public void A_move_an_enemy_made_only_in_a_trial_is_not_among_the_moves_used()
        {
            ScenarioOutcome outcome = ScenarioRunnerTests.Play(@"
card Smite
  cost 1
  target enemy
  effect:
    deal 30 to target

enemy Idol
  hp 20
  move Stare:
    deal 1 to player
  pattern cycle Stare

scenario ""Over before it started""
  runs 2
  player hp 40 energy 3
  deck 4 Smite
  battle Idol
", new ScenarioOptions().Bot(Bots.Cautious));

            Meter meter = Meter(outcome);
            Assert.Empty(meter.Moves);
            Assert.Equal(0L, meter.TotalTaken);

            // Smite says 30 and the Idol has 20, twice over: what is counted is hp actually lost,
            // which is what makes the sum comparable with the engine's own hp.
            Assert.Equal(40L, meter.DamageDealt.Single(t => t.Name == "Smite").Amount);
        }

        /// <summary>
        /// Every number in the tables is an amount the engine raised, and nothing else. This adds
        /// the raw events up by hand, from a host that saw each one as it arrived, and requires the
        /// meter to have reached exactly the same numbers.
        /// </summary>
        [Fact]
        public void Every_number_in_the_tables_comes_from_an_event_the_engine_raised()
        {
            var meter = new Echo();
            CardRuntime runtime = Fight(meter);

            // Against the raw events, one table at a time.
            Assert.Equal(
                meter.Raw.Where(e => e.Name == "damaged" && e.TargetTeam != Team.Player)
                    .GroupBy(e => e.Dealer).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{g.Key} {g.Sum(e => e.Amount)}"),
                meter.DamageDealt.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => $"{t.Name} {t.Amount}"));

            Assert.Equal(
                meter.Raw.Where(e => e.Name == "damaged" && e.TargetTeam == Team.Player)
                    .GroupBy(e => e.Dealer).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{g.Key} {g.Sum(e => e.Amount)}"),
                meter.DamageTaken.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => $"{t.Name} {t.Amount}"));

            Assert.Equal(
                (long)meter.Raw.Where(e => e.Name == "healed").Sum(e => e.Amount),
                meter.Healing.Sum(t => t.Amount));
            Assert.Equal(
                (long)meter.Raw.Where(e => e.Name == "gained_block").Sum(e => e.Amount),
                meter.Block.Sum(t => t.Amount));
            Assert.Equal(
                meter.Raw.Count(e => e.Name == "card_played"),
                meter.Cards.Sum(t => t.Times));
            Assert.Equal(
                meter.Raw.Count(e => e.Name == "status_applied"),
                meter.Statuses.Sum(t => t.Times));
            Assert.Equal(
                meter.Raw.Count(e => e.Name == "move"),
                meter.Moves.Sum(t => t.Times));

            // And against the content, which is where the raw events came from: Zap says 6, and
            // each hit landed on an enemy with more hp than that, so every one of them is 6.
            int zaps = meter.Cards.Single(t => t.Name == "Zap").Times;
            Assert.True(zaps > 0);
            Assert.Equal(6L * zaps, meter.DamageDealt.Single(t => t.Name == "Zap").Amount);
            Assert.Equal(4L * meter.Cards.Single(t => t.Name == "Ward").Times, meter.Block.Sum(t => t.Amount));

            // The enemy's hp is the last word: what it lost is what the table says was dealt to
            // it, less the healing its own Mend gave it back. That is conservation, on one actor.
            Entity dummy = runtime.State.Actors(Team.Enemy).Single();
            long mended = meter.Healing.Where(t => t.Name == "Dummy").Sum(t => t.Amount);
            Assert.True(mended > 0);
            Assert.Equal(200L - dummy.GetInt("hp"), meter.TotalDealt - mended);
        }

        /// <summary>
        /// A hit's tags are the hit's own, which is what lets the report say that nothing a card
        /// dealt was ever <c>fire</c>. Burn is the case worth pinning, because the slice tags it
        /// <c>burn</c> and deliberately not <c>fire</c>.
        /// </summary>
        [Fact]
        public void Damage_is_counted_under_every_tag_its_hit_carried_and_no_others()
        {
            ScenarioOutcome outcome = PlaySample("slice", Bots.Cautious, runs: 2);
            Meter meter = Meter(outcome);

            Assert.Contains(meter.DamageDealtByTag, t => t.Name == "burn");
            Assert.DoesNotContain(meter.DamageDealtByTag, t => t.Name == "fire");

            Tally burn = meter.DamageDealt.Single(t => t.Name == "Burn");
            Assert.Equal(new[] { "burn", "debuff", "dot" }, burn.Tags);
            Assert.False(burn.Carried("fire"));

            // A tag's row is the hp of every hit that carried it, so `burn` and Burn agree.
            Assert.Equal(burn.Amount, meter.DamageDealtByTag.Single(t => t.Name == "burn").Amount);

            // The finding the tag column exists for, and the claim it makes: whatever pair it
            // picks, no hit from that source ever carried that tag.
            (string Source, string Tag, double Share)? missing = meter.MissingTag();
            Assert.NotNull(missing);
            Assert.False(meter.DamageDealt.Single(t => t.Name == missing!.Value.Source).Carried(missing.Value.Tag));
            Assert.Contains(meter.DamageDealtByTag, t => t.Name == missing!.Value.Tag);
        }

        /// <summary>
        /// Same command, same tables. A count that fell out of a hash set's order, or a run that
        /// quietly carried something into the next one, would show here.
        /// </summary>
        [Fact]
        public void The_same_seeds_measure_the_same_thing_twice()
        {
            Assert.Equal(
                Written(PlaySample("slice", Bots.Cautious, runs: 3)),
                Written(PlaySample("slice", Bots.Cautious, runs: 3)));

            // A guard: a different seed really does measure something else, so the equality above
            // is not the equality of two empty tables.
            Assert.NotEqual(
                Written(PlaySample("slice", Bots.Cautious, runs: 3)),
                Written(PlaySample("slice", Bots.Cautious, runs: 3, firstSeed: 900)));
        }

        /// <summary>
        /// A card that asks the player to choose part way through its own effect is settled by a
        /// coin, so the report names it rather than letting the rest of the numbers be read as a
        /// measurement of it. A trial's choices are not among them, for the same reason its damage
        /// is not.
        /// </summary>
        [Fact]
        public void A_choice_nothing_could_judge_is_named_with_the_card_that_asked()
        {
            ScenarioOutcome outcome = ScenarioRunnerTests.Play(@"
card Rime
  cost 9
  tags tome
  effect:
    block 9

card Flare
  cost 9
  tags tome
  target enemy
  effect:
    deal 9 to target

card Study
  cost 1
  effect:
    discover 2 cards where tag:tome as found
    create found into hand

enemy Idol
  hp 200
  move Stare:
    block 1
  pattern cycle Stare

scenario ""Nothing here judges Study""
  runs 3
  player hp 40
  deck 4 Study
  battle Idol
", new ScenarioOptions { TurnLimit = 2 }.Bot(Bots.Cautious));

            Meter meter = Meter(outcome);
            ChoiceTally guessed = Assert.Single(meter.Choices);

            // Named after the card, and pointing at the `discover` line inside it.
            Assert.Equal("Study", guessed.Label);
            Assert.Equal(18, guessed.Span.Line);

            // Once per Study that was really played, and not once per Study the bot tried.
            Assert.True(guessed.Times > 0);
            Assert.Equal(meter.Cards.Single(t => t.Name == "Study").Times, guessed.Times);
        }

        // Fixtures ------------------------------------------------------------------------------

        private const string OneHit = @"
card Zap
  cost 1
  target enemy
  effect:
    deal 6 to target

enemy Idol
  hp 200
  move Stare:
    block 1
  pattern cycle Stare

scenario ""One hit""
  runs 1
  player hp 40 energy 3
  deck 4 Zap
  battle Idol
";

        private static ScenarioOutcome Play(string dsl, IBot bot)
        {
            var options = new ScenarioOptions { Runs = 1, TurnLimit = 1 };
            options.MakeBots.Clear();
            options.MakeBots.Add(_ => bot);
            return ScenarioRunnerTests.Play(dsl, options);
        }

        private static ScenarioOutcome PlaySample(string sample, string bot, int runs, ulong firstSeed = 1)
        {
            var content = new ContentLibrary();
            content.LoadFolder(Path.Combine(ScenarioRunnerTests.RepositoryRoot(), "samples", sample));
            content.Diagnostics.ThrowIfErrors();

            var options = new ScenarioOptions { Runs = runs, FirstSeed = firstSeed }.Bot(bot);
            return new ScenarioRunner(content, options).Run(content.Scenarios[0]);
        }

        private static Meter Meter(ScenarioOutcome outcome) => outcome.ByBot[0].Meter;

        /// <summary>Every table the meter has, as text, so two passes can be compared whole.</summary>
        private static string Written(ScenarioOutcome outcome)
        {
            Meter meter = Meter(outcome);
            IEnumerable<string> tables = new[]
                {
                    meter.DamageDealt, meter.DamageTaken, meter.DamageDealtByTag,
                    meter.Healing, meter.Block, meter.Statuses, meter.Cards, meter.Moves,
                }
                .Select(table => string.Join(", ", table.Select(t => $"{t.Name}={t.Amount}/{t.Times}")));
            return string.Join("\n", tables);
        }

        /// <summary>A short fight driven straight through the runtime, with no bot and no scenario.</summary>
        private static CardRuntime Fight(Meter meter)
        {
            ContentLibrary content = ScenarioRunnerTests.Load(@"
status Singe
  tags heat
  stacking intensity
  on turn_end:
    deal stacks to owner
    stacks -1

card Zap
  cost 1
  target enemy
  tags attack
  effect:
    deal 6 to target

card Ward
  cost 1
  effect:
    block 4

card Kindle
  cost 1
  target enemy
  effect:
    apply Singe 3 to target

enemy Dummy
  hp 200
  move Poke:
    deal 3 to player
  move Mend:
    heal 2
  pattern cycle Poke, Mend
");

            var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 7, Host = meter });
            runtime.CreatePlayer(hp: 60, maxEnergy: 3);
            runtime.AddDeck(new[] { "Zap", "Zap", "Ward", "Kindle", "Zap", "Ward", "Zap", "Kindle" });
            runtime.SpawnEnemy("Dummy");
            runtime.StartBattle();

            Entity enemy = runtime.State.Actors(Team.Enemy).Single();
            for (int turn = 0; turn < 6 && runtime.Won == null; turn++)
            {
                foreach (Entity card in runtime.State.ZoneOf(runtime.Player, Zones.Hand).ToArray())
                {
                    if (runtime.CanPlay(card)) runtime.Play(card, runtime.TargetMode(card) == "enemy" ? enemy : null);
                }
                runtime.EndTurn();
            }
            return runtime;
        }

        /// <summary>
        /// A bot that makes exactly one play, either inside a trial or for real, and nothing else.
        /// </summary>
        private sealed class OnePlayBot : IBot
        {
            private readonly bool _inATrial;
            private bool _done;

            public OnePlayBot(bool inATrial) => _inATrial = inATrial;

            public string Name => _inATrial ? "trial" : "play";

            public string Description => _inATrial ? "tries one play and rolls it back" : "makes one play";

            public IChoiceProvider Chooser { get; } = new RandomChooser(1);

            /// <summary>True once the engine has accepted the play, tried or made.</summary>
            public bool Played { get; private set; }

            public void PlayTurn(CardRuntime runtime, Trials trials, Action<string>? log)
            {
                if (_done) return;
                _done = true;

                List<Option> options = Options.Legal(runtime);
                if (!_inATrial)
                {
                    Played = Options.TakeAny(runtime, options, null);
                    return;
                }

                using (trials.Begin(0xF06UL))
                {
                    Played = Options.TakeAny(runtime, options, null);
                }
            }
        }

        /// <summary>
        /// A meter that also keeps the raw events, so the tables can be checked against the things
        /// they were made from. Overriding <c>OnEvent</c> is the seam: it is what any host that
        /// wants both the counting and the events themselves does.
        /// </summary>
        private sealed class Echo : Meter
        {
            public List<Seen> Raw { get; } = new List<Seen>();

            public override void OnEvent(GameEvent gameEvent)
            {
                // Recorded as it arrives: the event is finished by now, but nothing here relies on
                // it staying that way.
                if (Recording && !gameEvent.Replaced) Raw.Add(new Seen(gameEvent));
                base.OnEvent(gameEvent);
            }
        }

        /// <summary>One event, as it was when the engine handed it over.</summary>
        private sealed class Seen
        {
            public Seen(GameEvent gameEvent)
            {
                Name = gameEvent.Name;
                Dealer = gameEvent.Card?.Name ?? gameEvent.Source?.Name ?? "(nothing)";
                TargetTeam = gameEvent.Target?.Team ?? Team.Neutral;
                Amount = gameEvent.Amount.ToInt();
            }

            public string Name { get; }
            public string Dealer { get; }
            public Team TargetTeam { get; }
            public int Amount { get; }
        }
    }
}
