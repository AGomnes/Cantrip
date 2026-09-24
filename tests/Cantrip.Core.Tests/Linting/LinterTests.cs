using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Xunit;

namespace Cantrip.Tests.Linting
{
    public sealed class LinterTests
    {
        private const string Base = """
            status "Poison"
              tags dot, poison, debuff
              stacking intensity
              on turn_end:
                deal stacks to owner, ignore block
                stacks -1

            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Flame"
              cost 1
              target enemy
              tags attack, fire
              effect:
                deal 3 to target

            """;

        private static IReadOnlyList<Diagnostic> Lint(string dsl, LintOptions? options = null) =>
            Linter.Lint(ContentLibrary.FromText(Base + dsl, "lint.cantrip"), options);

        private static Diagnostic Single(IReadOnlyList<Diagnostic> diagnostics, string code)
        {
            var matching = diagnostics.Where(d => d.Code == code).ToList();
            Assert.True(matching.Count == 1, $"expected one {code}, got:\n{string.Join("\n", diagnostics)}");
            return matching[0];
        }

        private static void None(IReadOnlyList<Diagnostic> diagnostics, string code) =>
            Assert.True(diagnostics.All(d => d.Code != code), $"expected no {code}, got:\n{string.Join("\n", diagnostics)}");

        // CT301 unknown verb ------------------------------------------------------------------

        [Fact]
        public void A_misspelled_verb_is_an_error_with_a_suggestion()
        {
            Diagnostic d = Single(Lint("""
                card "Typo"
                  cost 1
                  target enemy
                  effect:
                    dael 5 to target
                """), Linter.UnknownVerb);

            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.Equal("deal", d.Suggestion);

            // The fifth line of the snippet, after the shared base content.
            int baseLines = Base.Split('\n').Length - 1;
            Assert.Equal(baseLines + 5, d.Span.Line);
        }

        /// <summary>
        /// <c>play</c> is the one word that is both a test verb and a rule verb, so it is no longer
        /// unknown outside a test: <c>play Strike</c> in a card effect is CT320, which says the same
        /// thing more exactly — a card cannot be played out of content, only out of a pile.
        /// </summary>
        [Fact]
        public void Test_verbs_are_only_known_inside_tests()
        {
            IReadOnlyList<Diagnostic> inTest = Lint("""
                test "fine"
                  enemy hp 10
                  play Strike on enemy
                  expect enemy.hp == 4
                """);
            None(inTest, Linter.UnknownVerb);
            None(inTest, Linter.ContentWhereSomethingInPlayIsMeant);

            IReadOnlyList<Diagnostic> inCard = Lint("""
                card "Cheat"
                  cost 0
                  effect:
                    play Strike
                """);
            None(inCard, Linter.UnknownVerb);
            Diagnostic outside = Single(inCard, Linter.ContentWhereSomethingInPlayIsMeant);
            Assert.Contains("`play` acts on a card that is in a pile", outside.Message);

            // The test verbs that are only test verbs are still unknown anywhere else.
            Diagnostic cast = Single(Lint("""
                card "Cheat"
                  cost 0
                  effect:
                    cast Surge
                """), Linter.UnknownVerb);
            Assert.Contains("only exists inside `test` blocks", cast.Message);
        }

        [Fact]
        public void Content_and_host_verbs_are_known()
        {
            var options = new LintOptions();
            options.HostVerbs.Add("corrupt");

            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                verb shatter(t):
                  deal 10 to t

                card "Breaker"
                  cost 1
                  target enemy
                  effect:
                    shatter target
                    corrupt 2
                """, options);

            None(diagnostics, Linter.UnknownVerb);
        }

        // CT302 unknown names -----------------------------------------------------------------

        [Fact]
        public void Applying_an_undefined_status_is_an_error()
        {
            Diagnostic d = Single(Lint("""
                card "Toxin"
                  cost 1
                  target enemy
                  effect:
                    apply Posion 3 to target
                """), Linter.UnknownName);

            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.Equal("Poison", d.Suggestion);
        }

        [Fact]
        public void Applying_a_card_as_a_status_is_an_error()
        {
            Diagnostic d = Single(Lint("""
                card "Confused"
                  cost 1
                  target enemy
                  effect:
                    apply Strike 1 to target
                """), Linter.UnknownName);

            Assert.Contains("is a card", d.Message);
        }

        [Fact]
        public void An_unknown_lowercase_name_is_a_warning_but_stats_locals_and_parameters_are_fine()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                verb splash(amount):
                  for each e in enemies:
                    deal amount to e

                card "Mistake"
                  cost 1
                  target enemy
                  effect:
                    deal dmage to target

                card "Fine"
                  cost 1
                  target enemy
                  effect:
                    deal block to target
                    splash 2
                    deal damage_taken_this_turn to target
                """);

            Diagnostic d = Single(diagnostics, Linter.UnknownName);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
            Assert.Contains("dmage", d.Message);
        }

        [Fact]
        public void Event_and_move_names_are_not_mistaken_for_values()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                card "Spark"
                  cost 0
                  effect:
                    emit sparked 1

                relic "Capacitor"
                  on sparked:
                    gain 1 energy

                enemy "Worm"
                  hp 20
                  on self.damaged:
                    use Chomp
                  move "Chomp":
                    deal 5 to player
                """);

            None(diagnostics, Linter.UnknownName);
        }

        [Fact]
        public void Reading_an_undefined_status_on_a_target_is_an_error()
        {
            Diagnostic d = Single(Lint("""
                card "Detox"
                  cost 1
                  target enemy
                  effect:
                    deal target.Posion to target
                """), Linter.UnknownName);

            Assert.Equal("Poison", d.Suggestion);
        }

        [Fact]
        public void Test_bindings_and_stats_set_in_tests_are_known()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                test "bindings"
                  enemy hp 10
                  enemy hp 10
                  player rage 5
                  expect enemy2.hp == 10 and player.rage == 5
                """);

            None(diagnostics, Linter.UnknownName);
        }

        // CT303 tags --------------------------------------------------------------------------

        [Fact]
        public void An_undeclared_tag_is_a_warning_with_a_suggestion()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                relic "Tinder"
                  on damaged(tag:frie):
                    gain 1 gold

                relic "Brutal"
                  on damaged(tag:attack):
                    gain 1 gold
                """);

            Diagnostic d = Single(diagnostics, Linter.UnknownTag);
            Assert.Equal("fire", d.Suggestion);
        }

        // CT304 / CT305 events ----------------------------------------------------------------

        [Fact]
        public void Listening_to_an_unknown_event_is_a_warning_with_a_suggestion()
        {
            Diagnostic d = Single(Lint("""
                relic "Typo"
                  on damagd:
                    gain 1 gold
                """), Linter.UnknownEvent);

            Assert.Equal("damaged", d.Suggestion);
        }

        [Fact]
        public void Emitted_events_and_known_stat_changes_can_be_listened_to()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                card "Charge"
                  cost 0
                  effect:
                    emit charged 2

                relic "Battery"
                  on charged:
                    gain 1 gold
                  on gold_changed:
                    log "rich"
                """);

            None(diagnostics, Linter.UnknownEvent);
            None(diagnostics, Linter.UnheardEvent);
        }

        [Fact]
        public void Listening_for_changes_to_an_unknown_stat_is_a_warning()
        {
            Single(Lint("""
                relic "Mystic"
                  on mana_changed:
                    gain 1 gold
                """), Linter.UnknownEvent);
        }

        [Fact]
        public void An_emitted_event_nobody_hears_is_a_note()
        {
            Diagnostic d = Single(Lint("""
                card "Shout"
                  cost 0
                  effect:
                    emit shouted
                """), Linter.UnheardEvent);

            Assert.Equal(DiagnosticSeverity.Info, d.Severity);
        }

        // CT306 cycles ------------------------------------------------------------------------

        [Fact]
        public void A_listener_that_can_retrigger_itself_is_noted()
        {
            Diagnostic d = Single(Lint("""
                relic "Echo"
                  on damaged:
                    deal 1 to target
                """), Linter.EventCycle);

            Assert.Equal(DiagnosticSeverity.Info, d.Severity);
            Assert.Contains("`damaged`", d.Message);
        }

        [Fact]
        public void Cycles_through_several_events_and_content_verbs_are_found_once()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                verb ping(t):
                  emit pinged to t

                relic "A"
                  on healed:
                    ping target

                relic "B"
                  on pinged:
                    heal 1
                """);

            Diagnostic d = Single(diagnostics, Linter.EventCycle);
            Assert.Contains("`healed`", d.Message);
            Assert.Contains("`pinged`", d.Message);
        }

        // CT307 / CT308 event misuse ----------------------------------------------------------

        [Fact]
        public void Event_outside_a_listener_is_an_error()
        {
            Diagnostic d = Single(Lint("""
                card "Confused"
                  cost 0
                  effect:
                    gain event.amount gold
                """), Linter.EventOutsideListener);

            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        }

        [Fact]
        public void Cancel_in_an_after_listener_is_an_error_but_fine_before()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                relic "Late"
                  on damaged:
                    cancel

                relic "Early"
                  on before_damaged:
                    cancel
                """);

            Diagnostic d = Single(diagnostics, Linter.CancelAfterEvent);
            Assert.Contains("before_damaged", d.Message);
        }

        // CT309 unused target -----------------------------------------------------------------

        [Fact]
        public void A_targeted_card_that_ignores_its_target_is_a_warning()
        {
            IReadOnlyList<Diagnostic> diagnostics = Lint("""
                card "Pointless"
                  cost 1
                  target enemy
                  effect:
                    block 5

                card "Implicit"
                  cost 1
                  target enemy
                  effect:
                    deal 5
                """);

            Diagnostic d = Single(diagnostics, Linter.UnusedTarget);
            Assert.Contains("Pointless", d.Message);
        }

        // CT310 / CT311 / CT312 ---------------------------------------------------------------

        [Fact]
        public void An_unused_content_verb_is_a_note()
        {
            Diagnostic d = Single(Lint("""
                verb lonely(t):
                  deal 1 to t
                """), Linter.UnusedVerb);

            Assert.Equal(DiagnosticSeverity.Info, d.Severity);
        }

        [Fact]
        public void Stacks_outside_a_status_is_a_warning()
        {
            Single(Lint("""
                relic "Confused"
                  on turn_start:
                    block stacks
                """), Linter.StacksOutsideStatus);
        }

        [Fact]
        public void Using_a_move_the_enemy_does_not_have_is_an_error()
        {
            Diagnostic d = Single(Lint("""
                enemy "Worm"
                  hp 20
                  on self.damaged:
                    use Chmop
                  move "Chomp":
                    deal 5 to player
                """), Linter.UnknownMove);

            Assert.Equal("Chomp", d.Suggestion);
        }

        // Options and samples -----------------------------------------------------------------

        [Fact]
        public void Suppressed_codes_are_left_out()
        {
            var options = new LintOptions();
            options.Suppressed.Add(Linter.EventCycle);

            None(Lint("""
                relic "Echo"
                  on damaged:
                    deal 1 to target
                """, options), Linter.EventCycle);
        }

        [Fact]
        public void The_sample_content_has_no_errors_or_warnings()
        {
            var content = new ContentLibrary();
            content.LoadFolder(Path.Combine(LintTestPaths.RepositoryRoot(), "samples", "basic"));
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());

            IReadOnlyList<Diagnostic> diagnostics = Linter.Lint(content);

            var problems = diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).ToList();
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void Broken_content_can_still_be_linted()
        {
            ContentLibrary content = ContentLibrary.FromText("card \"Half\n  cost (\n  effect:\n    deal\n", "broken.cantrip");
            Assert.True(content.Diagnostics.HasErrors);

            Exception? error = Record.Exception(() => Linter.Lint(content));
            Assert.Null(error);
        }
    }

    internal static class LintTestPaths
    {
        public static string RepositoryRoot()
        {
            for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Cantrip.sln"))) return directory.FullName;
            }
            throw new InvalidOperationException("Could not find the repository root.");
        }
    }
}
