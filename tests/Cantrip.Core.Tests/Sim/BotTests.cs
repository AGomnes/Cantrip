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
    /// The bots, and the line the report rests on. A bot may play badly; what it may not do is
    /// leave a mark on the facts. A play it only tried raises the same events as a real one, so
    /// these tests pin that none of them is recorded, that a trial cannot read the dice the run
    /// will roll, and that two bots given the same content really can answer differently.
    /// </summary>
    public sealed class BotTests
    {
        /// <summary>
        /// The fight is over on the first turn, so the Idol never once moves. A bot that looks
        /// ahead does end the turn — in a trial, where the Idol stares back — and that stare must
        /// not reach the report, or "every move fired" becomes true of any content at all.
        /// </summary>
        [Fact]
        public void A_move_an_enemy_made_only_in_a_trial_never_fired()
        {
            ScenarioOutcome result = ScenarioRunnerTests.Play(@"
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

            // The guard: the fight really did end before the Idol had a turn.
            Assert.All(result.Runs, run => Assert.True(run.Battles[0].Won));
            Assert.All(result.Runs, run => Assert.Equal(1, run.Battles[0].Turns));
            Assert.All(result.Runs, run => Assert.Equal(0, run.Battles[0].HpLost));

            Assert.Equal(new[] { ("Idol", "Stare") }, result.Facts.NeverFired);
        }

        /// <summary>
        /// What a bot tries, it tries on dice of its own, and the run's own dice are exactly where
        /// they were afterwards. Without the reseeding a bot could try a play, see the roll that
        /// decides it, and choose accordingly, which no player could do.
        /// </summary>
        [Fact]
        public void A_trial_rolls_its_own_dice_and_leaves_the_runs_untouched()
        {
            var spy = new SpyBot();
            var options = new ScenarioOptions { Runs = 1, TurnLimit = 3 };
            options.MakeBots.Clear();
            options.MakeBots.Add(seed => spy);

            ScenarioRunnerTests.Play(@"
card Zap
  cost 1
  target enemy
  effect:
    deal 1..6 to target

enemy Wall
  hp 500
  move Wait:
    block 1
  pattern cycle Wait

scenario ""Watching the dice""
  runs 1
  player hp 50 energy 3
  deck 6 Zap
  battle Wall
", options);

            Assert.NotEmpty(spy.Turns);
            Assert.All(spy.Turns, turn => Assert.NotEqual(turn.Before, turn.Inside));
            Assert.All(spy.Turns, turn => Assert.Equal(turn.Before, turn.After));
            Assert.All(spy.Turns, turn => Assert.True(turn.OpenInside));
            Assert.All(spy.Turns, turn => Assert.False(turn.OpenAfter));
        }

        /// <summary>
        /// An answer a scenario wrote is spent when it is used and cannot be given back, so a play
        /// a bot merely tried must not take it. Without the guard the trial takes "Rime" and the
        /// play that follows discovers whatever the bot fancies.
        /// </summary>
        [Fact]
        public void A_trial_does_not_spend_the_answers_a_scenario_wrote()
        {
            // Study costs a whole turn's energy, so each run plays it once and discovers once.
            ScenarioOutcome result = ScenarioRunnerTests.Play(@"
card Study
  cost 3
  effect:
    discover 2 cards where tag:tome as found
    create found into hand

card Rime
  cost 9
  tags tome
  effect:
    block 9

card Flare
  cost 9
  target enemy
  tags tome
  effect:
    deal 9 to target

enemy Idol
  hp 200
  move Stare:
    block 1
  pattern cycle Stare

scenario ""The answer the scenario wrote""
  runs 8
  player hp 40
  deck 4 Study
  answer ""Rime""
  battle Idol
", new ScenarioOptions { TurnLimit = 1 }.Bot(Bots.Cautious));

            // The guard: Study really was played, so there really was a discover to answer.
            Assert.Contains("Study", result.ByBot[0].Meter.Cards.Select(t => t.Name));

            // Every run discovered Rime, because every run still had the answer when it came to
            // play Study for real. Flare was on offer every time and was never taken; with the
            // trial spending the answer, half the runs would have created it instead.
            Assert.Contains("Rime", result.Facts.CardsOwned);
            Assert.DoesNotContain("Flare", result.Facts.CardsOwned);
        }

        /// <summary>
        /// Same seed, same bot, same run, whatever else is going on. It is what makes a seed in the
        /// report worth printing, and it has to hold for a bot that rolls dice of its own.
        /// </summary>
        [Theory]
        [InlineData(Bots.Cautious)]
        [InlineData(Bots.Patient)]
        [InlineData(Bots.Random)]
        public void The_same_seed_and_the_same_bot_play_the_same_run(string bot)
        {
            const string dsl = @"
card Zap
  cost 1
  target enemy
  effect:
    deal 1..6 to target

card Ward
  cost 1
  effect:
    block 4

enemy Slime
  hp 40
  move Splat:
    deal 1..8 to player
  pattern cycle Splat

scenario ""A fight with dice in it""
  runs 5
  player hp 50 energy 2
  deck 4 Zap, 4 Ward
  battle Slime
";

            string first = Transcript(ScenarioRunnerTests.Play(dsl, new ScenarioOptions { FirstSeed = 7 }.Bot(bot)));
            string again = Transcript(ScenarioRunnerTests.Play(dsl, new ScenarioOptions { FirstSeed = 7 }.Bot(bot)));

            Assert.Equal(first, again);
            Assert.NotEqual(first, Transcript(ScenarioRunnerTests.Play(dsl, new ScenarioOptions { FirstSeed = 400 }.Bot(bot))));
        }

        /// <summary>
        /// Every bot finishes samples/abilities: a fight with no cards in it, two abilities on
        /// cooldowns and an enemy that changes its moves when wounded. A bot that could only play
        /// cards would pass every turn and every run would be a stall.
        /// </summary>
        [Theory]
        [InlineData(Bots.Cautious)]
        [InlineData(Bots.Patient)]
        [InlineData(Bots.Random)]
        public void Every_bot_finishes_the_fight_with_no_cards_in_it(string bot)
        {
            var content = new ContentLibrary();
            content.LoadFolder(Path.Combine(ScenarioRunnerTests.RepositoryRoot(), "samples", "abilities"));
            content.Diagnostics.ThrowIfErrors();

            ScenarioDefinition scenario = Assert.Single(content.Scenarios);
            ScenarioOutcome result = new ScenarioRunner(content, new ScenarioOptions { Runs = 3 }.Bot(bot)).Run(scenario);

            Assert.Equal(0, result.Errors);
            Assert.Equal(0, result.Stalls);
            Assert.All(result.Runs, run => Assert.All(run.Battles, battle => Assert.NotNull(battle.Won)));
        }

        /// <summary>
        /// The two bots the report prints side by side can answer the same question differently,
        /// and here they do: Kindle deals nothing this turn and more than Zap over two, so the
        /// cautious bot never plays it, the patient bot always does, and one of them dies of it.
        /// This is what the report means when it says a level is a fact about the bot.
        /// </summary>
        [Fact]
        public void The_two_bots_disagree_about_a_card_that_pays_off_later()
        {
            // Four cards and a five-card hand, so both are in hand every turn whatever the shuffle
            // does, and each costs a whole turn's energy, so every turn is a choice between them.
            ScenarioOutcome result = ScenarioRunnerTests.Play(@"
status Burn
  tags dot, debuff
  stacking intensity
  on turn_end:
    deal stacks to owner, ignore block
    stacks -1

card Zap
  cost 3
  target enemy
  effect:
    deal 6 to target

card Kindle
  cost 3
  target enemy
  effect:
    apply Burn 4 to target

enemy Ember
  hp 60
  move Claw:
    deal 4 to player
  pattern cycle Claw

scenario ""A card that pays off later""
  runs 2
  player hp 60
  deck 2 Zap, 2 Kindle
  battle Ember
  battle Ember
", new ScenarioOptions());

            ScenarioResult cautious = result.ByBot.Single(b => b.Bot == Bots.Cautious);
            ScenarioResult patient = result.ByBot.Single(b => b.Bot == Bots.Patient);

            Assert.Equal(0, cautious.RunsWon);
            Assert.Equal(patient.Runs.Count, patient.RunsWon);
            Assert.True(
                patient.Runs[0].Battles[0].Turns < cautious.Runs[0].Battles[0].Turns,
                $"patient {patient.Runs[0].Battles[0].Turns} turns, cautious {cautious.Runs[0].Battles[0].Turns}");

            // Neither bot is right. They are two answers, and the report has to print both.
            Assert.Equal(0.0, cautious.Level);
            Assert.Equal(1.0, patient.Level);
        }

        private static string Transcript(ScenarioOutcome result) =>
            string.Join("\n", result.Runs.Select(run =>
                $"{run.Seed} {run.Won} {run.HpLeft} {run.Error} " +
                string.Join(",", run.Battles.Select(b => $"{b.Label}:{b.Won}:{b.Turns}:{b.HpLost}"))));

        /// <summary>
        /// A bot that plays nothing and only watches the dice: what they were before a trial, what
        /// they were inside it, and what they were once it was over.
        /// </summary>
        private sealed class SpyBot : IBot
        {
            public string Name => "spy";

            public string Description => "watches the dice and plays nothing";

            public IChoiceProvider Chooser { get; } = new RandomChooser(1);

            public List<Reading> Turns { get; } = new List<Reading>();

            public void PlayTurn(CardRuntime runtime, Trials trials, Action<string>? log)
            {
                var reading = new Reading { Before = runtime.State.Rng.GetState() };
                using (trials.Begin(0xF06UL))
                {
                    reading.Inside = runtime.State.Rng.GetState();
                    reading.OpenInside = trials.Open;
                }

                reading.After = runtime.State.Rng.GetState();
                reading.OpenAfter = trials.Open;
                Turns.Add(reading);
            }

            internal sealed class Reading
            {
                public (ulong S0, ulong S1, ulong S2, ulong S3) Before;
                public (ulong S0, ulong S1, ulong S2, ulong S3) Inside;
                public (ulong S0, ulong S1, ulong S2, ulong S3) After;
                public bool OpenInside;
                public bool OpenAfter;
            }
        }
    }
}
