using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Snapshots
{
    /// <summary>
    /// Work waiting in a save: <c>next turn:</c> and <c>in N turns:</c> blocks, whether content or
    /// <see cref="CardRuntime.Execute"/> scheduled them, and what a restore does when content has
    /// changed underneath them.
    /// </summary>
    public sealed class ScheduledWorkSaveTests
    {
        private const string File = "content.cantrip";

        private const string Common = """
            enemy "Dummy"
              hp 100

            card "Filler"
              cost 0

            card "Recycle"
              cost 0
              effect:
                choose 1 from hand as picked
                exhaust picked

            relic "Compass"
              on turn_start:
                choose 1 from enemies as picked
                deal 1 to picked

            """;

        private const string Prepare = Common + """
            card "Prepare"
              cost 0
              effect:
                next turn:
                  draw 1
            """;

        private static CardRuntime Start(string content, string[]? hand = null, IChoiceProvider? chooser = null, int enemies = 1)
        {
            var library = new ContentLibrary();
            library.LoadText(content, File);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 7, Chooser = chooser });
            runtime.CreatePlayer();
            for (int i = 0; i < enemies; i++) runtime.SpawnEnemy("Dummy");
            foreach (string card in hand ?? Array.Empty<string>()) runtime.AddCard(card, Zones.Hand);
            for (int i = 0; i < 12; i++) runtime.AddCard("Filler");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        private static string Save(CardRuntime runtime) => JsonSerializer.Serialize(runtime.Capture());

        private static CardRuntime Load(string json, string content)
        {
            var library = new ContentLibrary();
            library.LoadText(content, File);
            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 999 });
            runtime.Restore(JsonSerializer.Deserialize<GameSnapshot>(json)!);
            return runtime;
        }

        private static int Hand(CardRuntime runtime) => runtime.State.ZoneOf(runtime.Player, Zones.Hand).Count;

        private static int Gold(CardRuntime runtime) => runtime.Player!.GetInt("gold");

        private static void EndTurnAnsweringFirst(CardRuntime runtime)
        {
            runtime.EndTurn();
            while (runtime.Pending != null) runtime.Answer(runtime.Pending.Options[0].Id);
        }

        // Work scheduled by Execute --------------------------------------------------------------

        [Fact]
        [Trait("Regression", "execute-schedule-cannot-be-saved")]
        public void Work_scheduled_by_Execute_is_saved_and_runs_after_a_restore()
        {
            CardRuntime original = Start(Common);
            original.Execute("next turn:\n  draw 1");

            Assert.True(original.CanCapture);
            CardRuntime restored = Load(Save(original), Common);
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());

            original.EndTurn();
            restored.EndTurn();

            Assert.Equal(6, Hand(restored));   // the scheduled draw, then the usual five
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
        }

        [Fact]
        [Trait("Regression", "execute-schedule-cannot-be-saved")]
        public void Delayed_and_nested_blocks_from_Execute_survive_a_save_part_way_through()
        {
            CardRuntime original = Start(Common);
            original.Execute("in 2 turns:\n  gain 5 gold\nnext turn:\n  next turn:\n    gain 1 gold");
            original.EndTurn();   // the outer `next turn:` has run and scheduled the inner one
            Assert.Equal(2, original.State.Scheduled.Count);

            CardRuntime restored = Load(Save(original), Common);
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());

            for (int i = 0; i < 3; i++)
            {
                original.EndTurn();
                restored.EndTurn();
                Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
            }
            Assert.Equal(6, Gold(restored));
            Assert.Empty(restored.State.Scheduled);
        }

        [Fact]
        [Trait("Regression", "execute-schedule-cannot-be-saved")]
        public void Work_from_Execute_keeps_its_target_across_a_save()
        {
            CardRuntime original = Start(Common, enemies: 2);
            original.Execute("next turn:\n  deal 7 to target", target: original.State.Actors(Team.Enemy)[1]);

            CardRuntime restored = Load(Save(original), Common);
            original.EndTurn();
            restored.EndTurn();

            Assert.Equal(100, restored.State.Actors(Team.Enemy)[0].GetInt("hp"));
            Assert.Equal(93, restored.State.Actors(Team.Enemy)[1].GetInt("hp"));
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
        }

        [Fact]
        [Trait("Regression", "execute-schedule-cannot-be-saved")]
        public void Choices_can_still_be_answered_after_Execute_schedules_work()
        {
            CardRuntime runtime = Start(Common, new[] { "Recycle", "Filler" }, new DeferredChooser());
            runtime.Execute("next turn:\n  draw 1");

            Assert.Equal(PlayResult.ChoicePending, runtime.Play("Recycle"));
            Assert.Equal(PlayResult.Played, runtime.Answer(runtime.Pending!.Options[0].Id));
            Assert.Empty(runtime.State.ZoneOf(runtime.Player, Zones.Hand));

            EndTurnAnsweringFirst(runtime);
            Assert.Equal(6, Hand(runtime));
        }

        [Fact]
        [Trait("Regression", "execute-schedule-cannot-be-saved")]
        public void Work_from_Execute_can_be_saved_again_after_a_restore()
        {
            CardRuntime original = Start(Common);
            original.Execute("next turn:\n  next turn:\n    gain 1 gold");

            // The outer block runs in the restored game and schedules the inner one, which comes from
            // the statements the restore parsed again: a second save must hold it as well.
            CardRuntime once = Load(Save(original), Common);
            once.EndTurn();
            Assert.True(once.CanCapture);
            CardRuntime twice = Load(Save(once), Common);

            original.EndTurn();
            original.EndTurn();
            twice.EndTurn();

            Assert.Equal(1, Gold(twice));
            Assert.Equal(original.State.ComputeHash(), twice.State.ComputeHash());
        }

        [Fact]
        [Trait("Regression", "can-capture-disagrees-with-capture")]
        public void CanCapture_is_false_for_waiting_work_that_a_save_cannot_hold()
        {
            CardRuntime runtime = Start(Common);
            Entity player = runtime.Player!;

            // A block the game parsed and ran itself belongs to no content and no Execute call.
            runtime.Interpreter.Execute(CardRuntime.ParseStatements("next turn:\n  draw 1"), new EvalContext(player) { Source = player });

            Assert.False(runtime.CanCapture);
            var error = Assert.Throws<InvalidOperationException>(() => runtime.Capture());
            Assert.Contains("cannot be saved", error.Message);
        }

        // Content reloaded in a running game ----------------------------------------------------

        [Fact]
        [Trait("Regression", "reload-makes-pending-work-unsaveable")]
        public void Pending_work_can_still_be_saved_after_a_hot_reload()
        {
            CardRuntime runtime = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, runtime.Play("Prepare"));

            // Any edit to the file gives every definition in it new syntax, Prepare's block included.
            string edited = Prepare.Replace("hp 100", "hp 120");
            runtime.Content.LoadText(edited, File);
            runtime.ApplyContentChanges();

            Assert.True(runtime.CanCapture);
            CardRuntime restored = Load(Save(runtime), edited);
            runtime.EndTurn();
            restored.EndTurn();

            Assert.Equal(6, Hand(restored));
            Assert.Equal(runtime.State.ComputeHash(), restored.State.ComputeHash());
        }

        [Fact]
        [Trait("Regression", "reloaded-work-saved-under-another-definition")]
        public void Work_saved_after_a_hot_reload_belongs_to_the_card_that_scheduled_it()
        {
            // Alpha comes first and has the same block, so a search of all content finds Alpha's.
            string twins = Prepare + """

                card "Alpha"
                  cost 0
                  effect:
                    next turn:
                      draw 1
                """;
            CardRuntime runtime = Start(twins, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, runtime.Play("Prepare"));

            string edited = twins.Replace("hp 100", "hp 120");
            runtime.Content.LoadText(edited, File);
            runtime.ApplyContentChanges();
            string save = Save(runtime);

            // A later patch to Alpha alone leaves the work Prepare scheduled where it was.
            string patched = edited.Replace("""
                card "Alpha"
                  cost 0
                  effect:
                    next turn:
                      draw 1
                """, """
                card "Alpha"
                  cost 0
                  effect:
                    next turn:
                      draw 2
                """);
            Assert.NotEqual(edited, patched);
            CardRuntime restored = Load(save, patched);
            restored.EndTurn();

            Assert.Equal(6, Hand(restored));
        }

        [Fact]
        [Trait("Regression", "can-capture-disagrees-with-capture")]
        public void A_reload_that_changes_waiting_work_blocks_saves_until_that_work_has_run()
        {
            CardRuntime runtime = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, runtime.Play("Prepare"));

            runtime.Content.LoadText(Prepare.Replace("draw 1", "draw 2"), File);
            runtime.ApplyContentChanges();

            // The waiting block is no longer in the content, so a save could not rebuild it.
            Assert.False(runtime.CanCapture);
            var error = Assert.Throws<InvalidOperationException>(() => runtime.Capture());
            Assert.Contains("cannot be saved", error.Message);

            runtime.EndTurn();
            Assert.Equal(6, Hand(runtime));   // it ran as it was written: one card, then the usual five
            Assert.True(runtime.CanCapture);
            runtime.Capture();
        }

        [Fact]
        [Trait("Regression", "reload-makes-pending-work-unsaveable")]
        public void A_choice_after_a_reload_that_changed_pending_work_rolls_back_to_the_work_as_written()
        {
            CardRuntime runtime = Start(Prepare, new[] { "Prepare" }, new DeferredChooser(), enemies: 2);
            runtime.AddRelic("Compass");
            Assert.Equal(PlayResult.Played, runtime.Play("Prepare"));

            runtime.Content.LoadText(Prepare.Replace("draw 1", "draw 2"), File);
            runtime.ApplyContentChanges();

            // Compass asks at every turn start, so End Turn stops and rolls back more than once.
            runtime.EndTurn();
            Assert.NotNull(runtime.Pending);
            while (runtime.Pending != null) runtime.Answer(runtime.Pending.Options[0].Id);

            // Work already waiting runs as it was when it was scheduled.
            Assert.Equal(6, Hand(runtime));
        }

        // Content patched between a save and a load -------------------------------------------

        [Fact]
        [Trait("Regression", "scheduled-block-address-is-positional")]
        public void A_patch_that_moves_a_waiting_block_still_runs_that_block()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));
            string save = Save(original);

            // The patch puts another block where the waiting one was, and moves that one down.
            string patched = Common + """
                card "Prepare"
                  cost 0
                  effect:
                    next turn:
                      gain 5 gold
                    next turn:
                      draw 1
                """;
            CardRuntime restored = Load(save, patched);
            restored.EndTurn();

            Assert.Equal(0, Gold(restored));
            Assert.Equal(6, Hand(restored));
        }

        [Fact]
        [Trait("Regression", "scheduled-block-address-is-positional")]
        public void A_patch_that_adds_a_line_above_a_waiting_block_still_runs_that_block()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));
            string save = Save(original);

            string patched = Common + """
                card "Prepare"
                  cost 0
                  effect:
                    gain 5 gold
                    next turn:
                      draw 1
                """;
            CardRuntime restored = Load(save, patched);
            restored.EndTurn();

            Assert.Equal(0, Gold(restored));
            Assert.Equal(6, Hand(restored));
        }

        [Fact]
        [Trait("Regression", "state-hash-follows-block-position")]
        public void A_game_restored_after_a_patch_that_moves_a_waiting_block_hashes_like_the_original()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));

            // The block has moved down two lines, and runs just the same.
            string patched = Common + """
                card "Prepare"
                  cost 0
                  # Draws next turn.
                  effect:

                    next turn:
                      draw 1
                """;
            CardRuntime restored = Load(Save(original), patched);
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());

            original.EndTurn();
            restored.EndTurn();
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
        }

        [Fact]
        public void Two_games_waiting_on_different_statements_do_not_hash_the_same()
        {
            CardRuntime one = Start(Common);
            CardRuntime other = Start(Common);
            one.Execute("next turn:\n  draw 1");
            other.Execute("next turn:\n  draw 2");

            Assert.NotEqual(one.State.ComputeHash(), other.State.ComputeHash());
        }

        [Fact]
        [Trait("Regression", "scheduled-block-address-is-positional")]
        public void A_patch_that_removes_the_verb_holding_a_waiting_block_is_refused_by_name()
        {
            string planned = Common + """
                verb plan():
                  next turn:
                    draw 1
                """;
            CardRuntime original = Start(planned);
            original.Execute("plan");
            string save = Save(original);

            var error = Assert.Throws<InvalidOperationException>(() => Load(save, Common));
            Assert.Contains("verb `plan`", error.Message);
            Assert.Contains("not loaded", error.Message);
        }

        [Fact]
        [Trait("Regression", "scheduled-block-address-is-positional")]
        public void A_patch_that_changes_a_waiting_block_is_refused_by_name()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));
            string save = Save(original);

            var error = Assert.Throws<InvalidOperationException>(() => Load(save, Prepare.Replace("draw 1", "draw 2")));
            Assert.Contains("card \"Prepare\"", error.Message);
        }

        [Fact]
        public void A_refusal_names_the_definition_even_when_its_name_has_a_slash()
        {
            string two = Prepare + """

                card "Prepare/Deluxe"
                  cost 0
                  effect:
                    next turn:
                      draw 3
                """;
            CardRuntime original = Start(two, new[] { "Prepare/Deluxe" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare/Deluxe"));
            string save = Save(original);

            var error = Assert.Throws<InvalidOperationException>(() => Load(save, two.Replace("draw 3", "draw 4")));
            Assert.Contains("card \"Prepare/Deluxe\"", error.Message);
        }

        [Fact]
        [Trait("Regression", "scheduled-block-address-is-positional")]
        public void A_patch_that_removes_a_waiting_block_is_refused_by_name()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));
            string save = Save(original);

            string patched = Common + """
                card "Prepare"
                  cost 0
                  effect:
                    draw 1
                """;
            var error = Assert.Throws<InvalidOperationException>(() => Load(save, patched));
            Assert.Contains("card \"Prepare\"", error.Message);
        }

        [Fact]
        public void A_patch_that_only_reformats_a_waiting_block_is_accepted()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));
            string save = Save(original);

            string patched = Common + """
                card "Prepare"
                  cost 0
                  # A comment, and the block indented further: the same statements.
                  effect:
                      next turn:
                          draw 1
                """;
            CardRuntime restored = Load(save, patched);
            restored.EndTurn();

            Assert.Equal(6, Hand(restored));
        }

        [Fact]
        public void A_save_made_against_windows_line_endings_loads_against_unix_ones()
        {
            string unix = Prepare.Replace("\r\n", "\n");
            CardRuntime original = Start(unix.Replace("\n", "\r\n"), new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));

            CardRuntime restored = Load(Save(original), unix);
            restored.EndTurn();

            Assert.Equal(6, Hand(restored));
        }

        [Fact]
        public void A_save_whose_executed_statements_were_altered_is_refused()
        {
            CardRuntime original = Start(Common);
            original.Execute("next turn:\n  draw 1");
            string save = Save(original).Replace("draw 1", "draw 9");

            Assert.Throws<InvalidOperationException>(() => Load(save, Common));
        }

        [Fact]
        public void A_save_whose_executed_statements_no_longer_parse_is_refused()
        {
            CardRuntime original = Start(Common);
            original.Execute("next turn:\n  draw 1");
            string save = Save(original).Replace("draw 1", "draw (");

            var error = Assert.Throws<InvalidOperationException>(() => Load(save, Common));
            Assert.Contains("no longer parse", error.Message);
        }

        [Fact]
        [Trait("Regression", "executed-statements-accept-any-block")]
        public void Executed_statements_saved_as_work_that_does_not_wait_are_refused()
        {
            CardRuntime original = Start(Common);
            original.Execute("next turn:\n  draw 1");

            // Statements that schedule nothing, named as a whole: no Execute call leaves those waiting.
            JsonNode json = JsonNode.Parse(Save(original))!;
            JsonObject scheduled = Assert.Single(json["Scheduled"]!.AsArray())!.AsObject();
            scheduled["Statements"] = "gain 99 gold";
            scheduled["Block"] = BlockAddressBook.ExecuteRoot;
            scheduled["BlockHash"] = BlockHash.Of(CardRuntime.ParseStatements("gain 99 gold"));

            var error = Assert.Throws<InvalidOperationException>(() => Load(json.ToJsonString(), Common));
            Assert.Contains("altered", error.Message);
        }

        [Fact]
        [Trait("Regression", "executed-statements-accept-any-block")]
        public void Executed_statements_saved_without_their_block_hash_are_refused()
        {
            CardRuntime original = Start(Common);
            original.Execute("next turn:\n  draw 1");

            // Statements came with the hash, so no save holds one without the other.
            JsonNode json = JsonNode.Parse(Save(original))!;
            JsonObject scheduled = Assert.Single(json["Scheduled"]!.AsArray())!.AsObject();
            scheduled.Remove("BlockHash");
            scheduled["Statements"] = "next turn:\n  gain 99 gold";

            var error = Assert.Throws<InvalidOperationException>(() => Load(json.ToJsonString(), Common));
            Assert.Contains("altered", error.Message);
        }

        [Fact]
        public void Saves_without_block_hashes_still_load()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));

            // A save from before the hash was recorded: the address alone, unchecked.
            JsonNode json = JsonNode.Parse(Save(original))!;
            foreach (JsonNode? scheduled in json["Scheduled"]!.AsArray()) scheduled!.AsObject().Remove("BlockHash");

            CardRuntime restored = Load(json.ToJsonString(), Prepare);
            original.EndTurn();
            restored.EndTurn();

            Assert.Equal(6, Hand(restored));
            Assert.Equal(original.State.ComputeHash(), restored.State.ComputeHash());
        }

        // A refused restore ----------------------------------------------------------------------

        [Fact]
        [Trait("Regression", "refused-restore-tears-down-the-game")]
        public void A_restore_refused_for_a_changed_block_leaves_the_game_in_progress_untouched()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            Assert.Equal(PlayResult.Played, original.Play("Prepare"));
            GameSnapshot save = JsonSerializer.Deserialize<GameSnapshot>(Save(original))!;

            CardRuntime current = Start(Prepare.Replace("draw 1", "draw 2"), new[] { "Filler" });
            ulong before = current.State.ComputeHash();

            Assert.Throws<InvalidOperationException>(() => current.Restore(save));

            Assert.Equal(before, current.State.ComputeHash());
            Assert.Equal(PlayResult.Played, current.Play("Filler"));
        }

        [Fact]
        [Trait("Regression", "refused-restore-tears-down-the-game")]
        public void A_restore_refused_for_a_missing_definition_leaves_the_game_in_progress_untouched()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            GameSnapshot save = JsonSerializer.Deserialize<GameSnapshot>(Save(original))!;

            CardRuntime current = Start(Common, new[] { "Filler" });
            ulong before = current.State.ComputeHash();

            var error = Assert.Throws<InvalidOperationException>(() => current.Restore(save));
            Assert.Contains("card \"Prepare\"", error.Message);

            Assert.Equal(before, current.State.ComputeHash());
            Assert.Equal(PlayResult.Played, current.Play("Filler"));
        }

        [Fact]
        [Trait("Regression", "refused-restore-tears-down-the-game")]
        public void A_restore_refused_while_a_choice_is_pending_leaves_that_choice_to_answer()
        {
            CardRuntime original = Start(Prepare, new[] { "Prepare" });
            GameSnapshot save = JsonSerializer.Deserialize<GameSnapshot>(Save(original))!;

            CardRuntime current = Start(Common, new[] { "Recycle", "Filler" }, new DeferredChooser());
            Assert.Equal(PlayResult.ChoicePending, current.Play("Recycle"));
            ulong before = current.State.ComputeHash();

            Assert.Throws<InvalidOperationException>(() => current.Restore(save));

            Assert.Equal(before, current.State.ComputeHash());
            Assert.Equal(PlayResult.Played, current.Answer(current.Pending!.Options[0].Id));
            Assert.Empty(current.State.ZoneOf(current.Player, Zones.Hand));
        }
    }
}
