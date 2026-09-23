using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cantrip.Sim;

namespace Cantrip.Cli
{
    /// <summary>
    /// Writes what <c>sim</c> measured. The first block is what the content allows, which holds
    /// whichever bot played and is collected from all of them; then one block per bot, for what
    /// that bot did with it, which is a fact about the bot and says so.
    /// </summary>
    internal static class SimReport
    {
        private const int Indent = 4;

        /// <summary>How many rows of one meter table are printed before the rest are summed up.</summary>
        private const int MostRows = 12;

        /// <summary>The widest heading in the meter's tables, which sets the width of their first column.</summary>
        private const int MeterHeading = 42;

        public static void Print(ScenarioOutcome outcome)
        {
            var scenario = outcome.Scenario;
            string where = $"{scenario.File}:{scenario.Syntax.Span.Line}";
            List<RunResult> runs = outcome.Runs.ToList();
            ulong first = runs.Count == 0 ? 0 : runs.Min(r => r.Seed);
            ulong last = runs.Count == 0 ? 0 : runs.Max(r => r.Seed);
            string bots = string.Join(" and ", outcome.ByBot.Select(b => b.Bot));

            Console.WriteLine();
            Console.WriteLine(Pad(scenario.Name, 52) + where);
            Console.WriteLine(
                $"  {outcome.RunsEach} run(s){(outcome.ByBot.Count > 1 ? $" by each of {outcome.ByBot.Count} bots" : string.Empty)}, " +
                $"seeds {first}-{last}, turn limit {outcome.TurnLimit}, {bots} bot{(outcome.ByBot.Count > 1 ? "s" : string.Empty)}");
            Console.WriteLine($"  {outcome.BattleLines} battle(s) and {outcome.OtherLines} other statement(s)");

            Console.WriteLine();
            Console.WriteLine("  What the content allows, whatever the bot did");

            Threw(outcome);
            Stalled(outcome);
            Cards(outcome);
            Moves(outcome);
            if (outcome.EveryRunIdentical)
                Note($"every run came out the same way, so {outcome.RunsEach} runs said no more than 1 would");

            // Which side of the line each of those lines is on, printed where they are read rather
            // than only at the foot of a report a build server will have cut off.
            Console.WriteLine();
            Line(Indent, "Read it the right way round: what happened at least once happened in your content,");
            Line(Indent, $"whoever plays. What never happened may instead be something {Which(outcome)} never reached:");
            Line(Indent, "a fight ended in two turns never gets to the third move of a pattern.");

            // One width for every bot's table, so that two of them can be read against each other.
            int width = Math.Max(24, outcome.Runs.SelectMany(r => r.Battles).Select(b => b.Label.Length + 2).DefaultIfEmpty(0).Max());
            foreach (ScenarioResult bot in outcome.ByBot) Battles(bot, width);
            Levels(outcome);
            Expectations(outcome);
        }

        /// <summary>What the report calls whoever played, for a claim that is bounded by them.</summary>
        private static string Which(ScenarioOutcome outcome) => outcome.ByBot.Count > 1 ? "these bots" : "this bot";

        // The facts ---------------------------------------------------------------------------

        private static void Threw(ScenarioOutcome outcome)
        {
            List<(RunResult Run, string Bot)> threw = outcome.ByBot
                .SelectMany(bot => bot.Runs.Where(r => r.Error != null).Select(r => (Run: r, bot.Bot)))
                .ToList();

            if (threw.Count == 0)
            {
                Note("nothing threw");
                return;
            }

            Bad($"{threw.Count} run(s) threw");
            foreach (var group in threw.GroupBy(t => t.Run.Error, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
            {
                (RunResult run, string bot) = group.First();
                string statement = run.Statement == null ? string.Empty : $", `{run.Statement}`";
                Line(Indent + 2, $"seed {run.Seed} at {run.At}{statement}, {bot} bot" + Also(group.Count() - 1));
                Line(Indent + 4, group.Key!);
            }
        }

        private static void Stalled(ScenarioOutcome outcome)
        {
            var stalls = outcome.ByBot
                .SelectMany(bot => bot.Runs.SelectMany(r => r.Battles.Where(b => b.Won == null).Select(b => (Battle: b, r.Seed, bot.Bot))))
                .ToList();

            if (stalls.Count == 0)
            {
                Note($"no battle reached the turn limit of {outcome.TurnLimit}");
                return;
            }

            Bad($"{stalls.Count} battle(s) reached the turn limit of {outcome.TurnLimit} and never ended");
            foreach (var group in stalls.GroupBy(s => (s.Battle.Label, s.Bot)).OrderByDescending(g => g.Count()))
            {
                IEnumerable<ulong> seeds = group.Select(s => s.Seed).Take(5);
                Line(Indent + 2, $"{group.Key.Label}, {group.Key.Bot} bot, seeds {string.Join(", ", seeds)}{(group.Count() > 5 ? ", ..." : string.Empty)}");
            }
        }

        private static void Cards(ScenarioOutcome outcome)
        {
            // A fight with no cards in it is a real thing, and has nothing to say here.
            if (outcome.Facts.CardCount == 0) return;

            IReadOnlyList<string> never = outcome.Facts.NeverPlayable;
            IReadOnlyList<string> undrawn = outcome.Facts.NeverHeld;

            if (never.Count == 0) Note("every card the player held was playable at least once");
            else Bad($"{never.Count} card(s) were held but never playable: {string.Join(", ", never)}");

            if (undrawn.Count > 0) Bad($"{undrawn.Count} card(s) never reached a hand: {string.Join(", ", undrawn)}");
        }

        private static void Moves(ScenarioOutcome outcome)
        {
            IReadOnlyList<(string Enemy, string Move)> never = outcome.Facts.NeverFired;
            if (outcome.Facts.MoveCount == 0) return;

            if (never.Count == 0)
            {
                Note("every move of every enemy fought fired");
                return;
            }

            Bad($"{never.Count} enemy move(s) never fired");
            foreach (var group in never.GroupBy(m => m.Enemy, StringComparer.Ordinal))
                Line(Indent + 2, $"{group.Key}: {string.Join(", ", group.Select(m => m.Move))}");
        }

        // What each bot did ---------------------------------------------------------------------

        private static void Battles(ScenarioResult result, int width)
        {
            var battles = result.Runs.SelectMany(r => r.Battles).GroupBy(b => (b.Index, b.Label)).OrderBy(g => g.Key.Index).ToList();
            if (battles.Count == 0)
            {
                Damage(result);
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"  What the {result.Bot} bot did with it, in {Seconds(result.Elapsed)}");
            Console.WriteLine("    " + Pad("battle", width) + Right("fought", 8) + Right("won", 8) + Right("turns", 8) + Right("hp lost", 9));
            foreach (var group in battles)
            {
                Console.WriteLine(
                    "    " + Pad(group.Key.Label, width) +
                    Right(group.Count().ToString(CultureInfo.InvariantCulture), 8) +
                    Right(Percent(group.Count(b => b.Won == true) / (double)group.Count()), 8) +
                    Right(group.Average(b => b.Turns).ToString("0.0", CultureInfo.InvariantCulture), 8) +
                    Right(group.Average(b => b.HpLost).ToString("0.0", CultureInfo.InvariantCulture), 9));
            }

            Damage(result);
        }

        /// <summary>
        /// The meter: what the engine raised while the game was really being played. It is the one
        /// block here that is not a judgement about play, which is why it says so at the top — and
        /// why it sits inside the bot's block all the same. Every amount in it is the engine's own,
        /// and the plays those amounts came from are the bot's.
        /// </summary>
        private static void Damage(ScenarioResult result)
        {
            Meter meter = result.Meter;
            if (meter.IsEmpty) return;

            Console.WriteLine();
            Console.WriteLine($"  Where the hp went, under the {result.Bot} bot");
            Line(Indent, "Every amount here is one the engine raised, so it holds for anyone who made these");
            Line(Indent, "plays. Which plays were made is this bot's doing; what each one cost is your content's.");

            // One width for all three tables, so the columns line up down the block.
            int width = Math.Max(
                MeterHeading,
                meter.DamageDealt.Concat(meter.DamageDealtByTag).Concat(meter.DamageTaken)
                    .Select(t => t.Name.Length + 4).DefaultIfEmpty(0).Max());

            Table("dealt to the enemies, by what dealt it", meter.DamageDealt, meter.TotalDealt, width);
            Table("the same hits, by the tags they carried", meter.DamageDealtByTag, meter.TotalDealt, width);
            Table("taken by the player, by what dealt it", meter.DamageTaken, meter.TotalTaken, width);

            (string Source, string Tag, double Share)? finding = meter.MissingTag();
            if (finding != null)
            {
                // Scoped to the plays that were made, both halves of it: the share is this bot's
                // mix, and a tag no hit carried is a tag no hit here carried.
                (string source, string tag, double share) = finding.Value;
                Console.WriteLine();
                Line(Indent, $"Of the damage these plays dealt, {source} is {Percent(share)}, and not one of its hits");
                Line(Indent, $"carried `{tag}`: `modify damage where tag:{tag}` would have reached none of it.");
            }

            Rest(meter, width);
            Guessed(meter);
        }

        /// <summary>One block of the meter, biggest first, with each row's share of the whole.</summary>
        private static void Table(string heading, IReadOnlyList<Tally> rows, long total, int width)
        {
            if (rows.Count == 0 || total <= 0) return;

            Console.WriteLine();
            Line(Indent, Pad(heading, width) + Right("hp", 8) + Right("share", 8));
            foreach (Tally row in rows.Take(MostRows))
            {
                Line(Indent + 2, Pad(row.Name, width - 2) +
                    Right(row.Amount.ToString(CultureInfo.InvariantCulture), 8) +
                    Right(Percent((double)row.Amount / total), 8));
            }
            if (rows.Count > MostRows) Line(Indent + 2, $"and {rows.Count - MostRows} more");
        }

        /// <summary>
        /// The rest of what the engine raised, in one line each. They are counts rather than hp, so
        /// they are not put in a column beside damage as though they were the same thing.
        /// </summary>
        private static void Rest(Meter meter, int width)
        {
            long healed = Sum(meter.Healing);
            long block = Sum(meter.Block);

            var rows = new List<(string What, long Amount, string Of)>();
            if (healed > 0) rows.Add(("healing", healed, "hp restored"));
            if (block > 0) rows.Add(("block", block, "gained"));
            if (meter.Statuses.Count > 0) rows.Add(("statuses", Times(meter.Statuses), $"applied, of {meter.Statuses.Count} kind(s)"));
            if (meter.Cards.Count > 0) rows.Add(("cards", Times(meter.Cards), $"played, for {Sum(meter.Cards)} energy"));
            if (meter.Moves.Count > 0) rows.Add(("enemy moves", Times(meter.Moves), $"used, of {meter.Moves.Count} kind(s)"));
            if (rows.Count == 0) return;

            Console.WriteLine();
            Line(Indent, "also counted, over the same plays");
            foreach ((string what, long amount, string of) in rows)
                Line(Indent + 2, Pad(what, width - 2) + Right(amount.ToString(CultureInfo.InvariantCulture), 8) + "  " + of);
        }

        /// <summary>
        /// What this bot could not judge. A card that asks the player to choose part way through
        /// its own effect is settled by a coin here, so what happened after it is luck, and the
        /// report names the card rather than letting the numbers stand as a measurement of it.
        /// </summary>
        private static void Guessed(Meter meter)
        {
            if (meter.Choices.Count == 0) return;

            // Plain rather than coloured: the yellow lines above are things found in the content,
            // and this is a limit of the bot.
            Console.WriteLine();
            Line(Indent, $"{meter.Choices.Count} line(s) asked for a choice mid-effect, which this bot answered at random");
            foreach (ChoiceTally choice in meter.Choices.Take(MostRows))
                Line(Indent + 2, $"{choice.Label} at {choice.Span}, {choice.Times} time(s)");
            if (meter.Choices.Count > MostRows) Line(Indent + 2, $"and {meter.Choices.Count - MostRows} more");
            Line(Indent + 2, "Nothing above judges those: a bot looks one play ahead, not into the middle of one.");
        }

        private static long Sum(IReadOnlyList<Tally> rows) => rows.Sum(r => r.Amount);

        private static int Times(IReadOnlyList<Tally> rows) => rows.Sum(r => r.Times);

        /// <summary>
        /// How often each bot finished the whole scenario. It is the number people quote and the
        /// one they should not, so it is printed last, under its own heading, with both bots' and
        /// the distance between them.
        /// </summary>
        private static void Levels(ScenarioOutcome outcome)
        {
            var levels = outcome.ByBot.Where(b => b.Level != null).ToList();
            if (levels.Count == 0) return;

            Console.WriteLine();
            Console.WriteLine("  Levels, for reference only");
            foreach (ScenarioResult bot in levels)
                Line(Indent, Pad($"{bot.Bot} bot", 24) + Right(Percent(bot.Level!.Value), 8) + $"   of {bot.Runs.Count} run(s) won");

            if (levels.Count < 2)
            {
                Line(Indent, "A level is a fact about the bot, not about your content. `--bot both` plays the");
                Line(Indent, "same content with a second bot, and the distance between the two is how much of");
                Line(Indent, "this number belongs to the bot rather than to you.");
                return;
            }

            double spread = (levels.Max(b => b.Level!.Value) - levels.Min(b => b.Level!.Value)) * 100;
            Line(Indent, spread > 0
                ? $"These bots played the same content, from the same seeds, and are {spread.ToString("0.0", CultureInfo.InvariantCulture)} points apart."
                : "These bots played the same content, from the same seeds, and happened to agree.");
            Line(Indent, "A level is a fact about the bot, not about your content: do not quote one, and");
            Line(Indent, "do not compare one with another bot's or with another release's.");
        }

        private static void Expectations(ScenarioOutcome outcome)
        {
            if (outcome.Expectations.Count == 0) return;

            Console.WriteLine();
            int width = Math.Max(24, outcome.Expectations.Max(e => e.Text.Length) + 2);
            foreach (ExpectResult expectation in outcome.Expectations)
            {
                Console.Write("  " + Pad(expectation.Text, width));
                if (expectation.Held == true)
                {
                    In(ConsoleColor.Green, "ok");
                    Console.WriteLine();
                    continue;
                }

                if (expectation.Held == false) In(ConsoleColor.Red, "failed");
                else In(ConsoleColor.Yellow, "-");
                Console.WriteLine(expectation.Detail.Length == 0 ? string.Empty : "   " + expectation.Detail);
            }
        }

        /// <summary>The last lines: what every scenario came to, and what the bots' part in it was.</summary>
        public static void Summary(IReadOnlyList<ScenarioOutcome> outcomes)
        {
            int runs = outcomes.Sum(o => o.Runs.Count());
            int errors = outcomes.Sum(o => o.Errors);
            int stalls = outcomes.Sum(o => o.Stalls);
            int failed = outcomes.Sum(o => o.Expectations.Count(e => e.Held == false));
            int notChecked = outcomes.Sum(o => o.Expectations.Count(e => e.Held == null));

            Console.WriteLine();
            Console.WriteLine(
                $"{outcomes.Count} scenario(s), {runs} run(s), {errors} error(s), {stalls} stall(s), " +
                $"{failed} failed expectation(s)" + (notChecked == 0 ? "." : $", {notChecked} not checked."));

            if (outcomes.Count == 0) return;

            List<ScenarioResult> bots = outcomes[0].ByBot.ToList();
            Console.WriteLine();
            foreach (ScenarioResult bot in bots) Console.WriteLine($"The {bot.Bot} bot {bot.BotDescription}.");

            Console.WriteLine("How far a run got, how long it took and what it cost are facts about the bot that played.");
            if (bots.Count > 1)
            {
                Console.WriteLine("Every bot here weighs a position the same way, the player's hp against the enemies',");
                Console.WriteLine("so two of them agreeing is not evidence: both are wrong in the same direction about a");
                Console.WriteLine("card that draws, and about anything that pays off several turns later.");
            }

            Console.WriteLine("Where the hp went is the engine's own arithmetic over the plays that were made, so the");
            Console.WriteLine("amounts are your content's and the mix of them is the bot's.");
        }

        // Writing -----------------------------------------------------------------------------

        private static void Note(string text) => Line(Indent, text);

        private static void Bad(string text)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Line(Indent, text);
            Console.ResetColor();
        }

        private static void In(ConsoleColor colour, string text)
        {
            Console.ForegroundColor = colour;
            Console.Write(text);
            Console.ResetColor();
        }

        private static void Line(int indent, string text) => Console.WriteLine(new string(' ', indent) + text);

        private static string Also(int more) => more == 0 ? string.Empty : $" and {more} more";

        private static string Pad(string text, int width) => text.Length >= width ? text + "  " : text.PadRight(width);

        private static string Right(string text, int width) => text.PadLeft(width);

        private static string Percent(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
    }
}
