using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Linting
{
    /// <summary>
    /// CT313: a line in a declaration that ends in a colon but is not a block Cantrip runs. It loads
    /// as a label, so a listener written another way used to load cleanly and never fire.
    /// </summary>
    public sealed class LabelLintTests
    {
        private static IReadOnlyList<Diagnostic> Lint(string dsl, LintOptions? options = null) =>
            Linter.Lint(ContentLibrary.FromText(dsl, "labels.cantrip"), options);

        private static List<Diagnostic> Labels(string dsl, LintOptions? options = null) =>
            Lint(dsl, options).Where(d => d.Code == Linter.UnknownBlock).ToList();

        private static string Relic(string header) => $$"""
            relic Anchor
              {{header}}
                gain 1 charge

            card Zap
              cost 0
              effect:
                draw 1

            test "the relic counts each card played"
              enemy hp 10
              relic Anchor
              play Zap
              expect player.charge == 1
            """;

        [Fact]
        public void A_listener_written_without_on_loads_and_never_fires_but_is_now_reported()
        {
            // Written the right way, the relic works and nothing is reported.
            ContentLibrary right = ContentLibrary.FromText(Relic("on card_played:"), "labels.cantrip");
            Assert.True(new DslTestRunner(right).RunAll().Single().Passed);
            Assert.DoesNotContain(Linter.Lint(right), d => d.Code == Linter.UnknownBlock);

            // Written another way, it still loads without a word, and the relic does nothing.
            ContentLibrary wrong = ContentLibrary.FromText(Relic("when card_played:"), "labels.cantrip");
            Assert.False(wrong.Diagnostics.HasErrors, wrong.Diagnostics.ToString());
            Assert.False(new DslTestRunner(wrong).RunAll().Single().Passed);

            Diagnostic label = Assert.Single(Linter.Lint(wrong), d => d.Code == Linter.UnknownBlock);
            Assert.Equal(DiagnosticSeverity.Warning, label.Severity);
            Assert.Equal(2, label.Span.Line);
            Assert.Equal("on card_played:", label.Suggestion);
            Assert.Contains("`when card_played:` in relic \"Anchor\" never runs", label.Message);
        }

        [Theory]
        [InlineData("when card_played:", "on card_played:")]
        [InlineData("whenever card_played(tag:attack):", "on card_played(tag:attack):")]
        [InlineData("once per battle on card_played:", "on card_played once per battle:")]
        [InlineData("once per turn on card_played(tag:attack):", "on card_played(tag:attack) once per turn:")]
        [InlineData("on_turn_start:", "on turn_start:")]
        [InlineData("at turn start:", "on turn_start:")]
        [InlineData("turn_end:", "on turn_end:")]
        [InlineData("before owner.damaged:", "on before_owner.damaged:")]
        [InlineData("instead of damaged:", "on instead_of_damaged:")]
        [InlineData("after card_played:", "on card_played:")]
        [InlineData("every 2 turns:", "on every 2 turns:")]
        [InlineData("whenever sparked:", "on sparked:")]
        [InlineData("when turn starts:", "on turn_start:")]
        [InlineData("when card_plyed(tag:attack):", "on card_played(tag:attack):")]
        [InlineData("once per turn on card_plyd:", "on card_played once per turn:")]
        [InlineData("whenever my_event:", "on my_event:")]
        public void A_misworded_listener_suggests_the_listener_it_looks_like(string header, string suggestion)
        {
            Diagnostic label = Assert.Single(Labels($$"""
                relic Anchor
                  {{header}}
                    draw 1

                card Spark
                  cost 0
                  effect:
                    emit sparked
                """));

            Assert.Equal(suggestion, label.Suggestion);
        }

        /// <summary>
        /// The suggestion for <c>once per battle on before_damaged(target:owner):</c> used to drop
        /// <c>before_</c>, and the listener it suggested, <c>on damaged(...)</c>, then failed CT308
        /// because it can no longer <c>cancel</c>. A timing written into the event word, and a filter,
        /// stay in the suggestion, which then loads and lints cleanly.
        /// </summary>
        [Theory]
        [Trait("Regression", "ct313-drops-timing")]
        [InlineData("once per battle on before_damaged(target:owner):", "on before_damaged(target:owner) once per battle:")]
        [InlineData("once per turn on instead_of_damaged(target:owner):", "on instead_of_damaged(target:owner) once per turn:")]
        [InlineData("once per battle on owner.before_damaged:", "on owner.before_damaged once per battle:")]
        [InlineData("once per battle on before_owner.damaged:", "on before_owner.damaged once per battle:")]
        [InlineData("when before_damaged(source:enemies):", "on before_damaged(source:enemies):")]
        [InlineData("whenever instead_of_damaged:", "on instead_of_damaged:")]
        [InlineData("on_before_damaged:", "on before_damaged:")]
        [InlineData("before_damaged:", "on before_damaged:")]
        [InlineData("when before_damagd(target:owner):", "on before_damaged(target:owner):")]
        [InlineData("before damaged(target:owner) once per battle:", "on before_damaged(target:owner) once per battle:")]
        [InlineData("once per battle before damaged(target:owner):", "on before_damaged(target:owner) once per battle:")]
        [InlineData("once per battle instead of died(target:owner):", "on instead_of_died(target:owner) once per battle:")]
        [InlineData("instead_of died(target:owner) once per battle:", "on instead_of_died(target:owner) once per battle:")]
        [InlineData("when before owner.damaged:", "on before_owner.damaged:")]
        public void A_suggestion_keeps_the_timing_and_filter_written_with_the_event(string header, string suggestion)
        {
            Diagnostic label = Assert.Single(Labels($$"""
                relic Amulet
                  {{header}}
                    cancel
                """));

            Assert.Equal(suggestion, label.Suggestion);

            // Taking the suggestion gives a listener that may cancel, and nothing to report.
            ContentLibrary fixedContent = ContentLibrary.FromText("relic Amulet\n  " + suggestion + "\n    cancel\n", "labels.cantrip");
            Assert.False(fixedContent.Diagnostics.HasErrors, fixedContent.Diagnostics.ToString());
            Assert.Empty(Linter.Lint(fixedContent));
        }

        [Theory]
        [InlineData("when start of turn:", "on turn_start:")]
        [InlineData("at end_of_turn:", "on turn_end:")]
        [InlineData("when card_play once per turn:", "on card_played once per turn:")]
        public void A_suggestion_finds_an_event_written_in_other_words(string header, string suggestion)
        {
            Diagnostic label = Assert.Single(Labels($$"""
                relic Anchor
                  {{header}}
                    draw 1
                """));

            Assert.Equal(suggestion, label.Suggestion);
        }

        [Fact]
        public void A_misspelt_block_suggests_the_block()
        {
            List<Diagnostic> labels = Labels("""
                card Zap
                  cost 0
                  effects:
                    draw 1

                enemy Brute
                  hp 10
                  moves "Hit":
                    deal 1 to player
                  move "Slam":
                    deal 2 to player
                """);

            Assert.Equal(new[] { "effect:", "move \"Hit\":" }, labels.Select(d => d.Suggestion));
        }

        /// <summary>
        /// A header that is prose, rather than an event in other words, gets no suggestion: taking
        /// one of its words as the event would name one that does not exist.
        /// </summary>
        [Theory]
        [InlineData("flurb:")]
        [InlineData("whenever player takes damage:")]
        [InlineData("when my turn is over:")]
        public void A_label_nothing_resembles_is_reported_without_a_suggestion(string header)
        {
            Diagnostic label = Assert.Single(Labels($$"""
                status Glow
                  {{header}}
                    draw 1
                """));

            Assert.Null(label.Suggestion);
        }

        [Fact]
        public void Blocks_the_engine_runs_and_real_listeners_are_not_reported()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                card Zap
                  cost 0
                  effect:
                    draw 1

                ability Nova
                  cooldown 2 turns
                  effect: draw 1

                enemy Brute
                  hp 10
                  phase Angry when hp <= 5
                  move "Hit" weight 2:
                    deal 1 to player
                  move "Slam" phase Angry:
                    deal 2 to player

                relic Anchor
                  on turn_start:
                    draw 1
                  on card_played(tag:attack) once per battle:
                    draw 1

                test "labelled groups inside a test still run"
                  setup:
                    enemy hp 10
                  expect enemy.hp == 10
                """);

            Assert.DoesNotContain(diagnostics, d => d.Code == Linter.UnknownBlock);
        }

        [Fact]
        public void A_block_the_game_runs_from_csharp_can_be_declared()
        {
            const string dsl = """
                card Scout
                  cost 0
                  effect:
                    draw 1
                  on_reveal:
                    draw 1
                """;

            Assert.Single(Labels(dsl));

            var options = new LintOptions();
            options.HostBlocks.Add("on_reveal");
            Assert.Empty(Labels(dsl, options));
        }

        [Theory]
        [Trait("Regression", "ct313-suggests-every-without-interval")]
        [InlineData("on_reveal:")]
        [InlineData("reveal:")]
        [InlineData("evry:")]
        public void A_label_is_never_told_to_become_an_every_listener_without_an_interval(string header)
        {
            // `on every:` does not parse: `every` needs an interval, such as `on every 2 turns:`.
            Diagnostic label = Assert.Single(Labels($$"""
                card Scout
                  cost 0
                  {{header}}
                    draw 1
                """));

            Assert.NotEqual("on every:", label.Suggestion);
            if (label.Suggestion != null) Assert.False(ContentLibrary.FromText("relic Probe\n  " + label.Suggestion + "\n    draw 1\n").Diagnostics.HasErrors);
        }

        [Fact]
        public void The_warning_can_be_suppressed()
        {
            var options = new LintOptions();
            options.Suppressed.Add("CT313");

            Assert.Empty(Labels("""
                relic Anchor
                  when card_played:
                    draw 1
                """, options));
        }

        [Theory]
        [InlineData("basic")]
        [InlineData("corpus")]
        [InlineData("slice")]
        [InlineData("recipes")]
        public void No_sample_has_a_block_that_never_runs(string folder)
        {
            var content = new ContentLibrary();
            content.LoadFolder(Path.Combine(LintTestPaths.RepositoryRoot(), "samples", folder));

            var found = Linter.Lint(content).Where(d => d.Code == Linter.UnknownBlock).ToList();
            Assert.True(found.Count == 0, string.Join("\n", found));
        }
    }
}
