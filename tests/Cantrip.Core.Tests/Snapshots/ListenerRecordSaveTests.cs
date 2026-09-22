using System.Text.Json;
using System.Text.Json.Nodes;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Snapshots
{
    /// <summary>
    /// What a save remembers about listeners: the window a <c>once per ...</c> listener last fired
    /// in, and when an <c>on every ...:</c> listener is next due. Each belongs to one listener, and
    /// a content patch that adds, removes, reorders or changes <c>on</c> blocks must not hand it to
    /// another.
    /// </summary>
    public sealed class ListenerRecordSaveTests
    {
        private const string File = "content.cantrip";

        private const string Common = """
            enemy "Dummy"
              hp 100

            card "Filler"
              cost 0

            card "Strike"
              cost 0
              tags attack
              effect:
                deal 1 to target

            """;

        private const string Tally = Common + """
            relic "Tally"
              on card_played once per battle:
                gain 1 gold
            """;

        private static CardRuntime Start(string content, string relic, RuntimeOptions? options = null)
        {
            var library = new ContentLibrary();
            library.LoadText(content, File);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, options ?? new RuntimeOptions { Seed = 7 });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            for (int i = 0; i < 4; i++) runtime.AddCard("Filler", Zones.Hand);
            for (int i = 0; i < 4; i++) runtime.AddCard("Strike", Zones.Hand);
            for (int i = 0; i < 12; i++) runtime.AddCard("Filler");
            runtime.AddRelic(relic);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        private static string Save(CardRuntime runtime) => JsonSerializer.Serialize(runtime.Capture());

        private static CardRuntime Load(string json, string content, RuntimeOptions? options = null)
        {
            var library = new ContentLibrary();
            library.LoadText(content, File);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, options ?? new RuntimeOptions { Seed = 999 });
            runtime.Restore(JsonSerializer.Deserialize<GameSnapshot>(json)!);
            return runtime;
        }

        private static int Gold(CardRuntime runtime) => runtime.Player!.GetInt("gold");

        private static void PlayFiller(CardRuntime runtime) => Assert.Equal(PlayResult.Played, runtime.Play("Filler"));

        private static void PlayStrike(CardRuntime runtime) =>
            Assert.Equal(PlayResult.Played, runtime.Play("Strike", runtime.State.Actors(Team.Enemy)[0]));

        /// <summary>A save that has used Tally's once-per-battle listener, as 0.1.0-preview.2 and later write it.</summary>
        private static string UsedTally(string content = Tally, string relic = "Tally")
        {
            CardRuntime original = Start(content, relic);
            PlayFiller(original);
            return Save(original);
        }

        // Limits ---------------------------------------------------------------------------------

        [Fact]
        public void A_used_limit_survives_a_save_into_the_same_content()
        {
            CardRuntime restored = Load(UsedTally(), Tally);
            Assert.Equal(1, Gold(restored));

            PlayFiller(restored);

            Assert.Equal(1, Gold(restored));
        }

        [Fact]
        [Trait("Regression", "listener-record-is-positional")]
        public void A_patch_that_adds_an_on_block_above_a_used_listener_keeps_its_limit_where_it_was()
        {
            string save = UsedTally();

            string patched = Common + """
                relic "Tally"
                  on card_played once per battle:
                    gain 10 gold
                  on card_played once per battle:
                    gain 1 gold
                """;
            CardRuntime restored = Load(save, patched);
            PlayFiller(restored);

            // The new listener fires for the first time; the one already used this battle does not.
            Assert.Equal(11, Gold(restored));
        }

        [Fact]
        [Trait("Regression", "listener-record-is-positional")]
        public void A_patch_that_removes_an_on_block_above_a_used_listener_keeps_its_limit()
        {
            string before = Common + """
                relic "Tally"
                  on card_exhausted:
                    gain 100 gold
                  on card_played once per battle:
                    gain 1 gold
                """;
            string save = UsedTally(before);

            CardRuntime restored = Load(save, Tally);
            PlayFiller(restored);

            Assert.Equal(1, Gold(restored));
        }

        [Fact]
        [Trait("Regression", "listener-record-is-positional")]
        public void A_patch_that_reorders_on_blocks_keeps_each_limit_with_its_listener()
        {
            string before = Common + """
                relic "Tally"
                  on card_played once per battle:
                    gain 1 gold
                  on card_played(tag:attack) once per battle:
                    gain 10 gold
                """;
            string save = UsedTally(before);   // a Filler is no attack, so only the first has fired

            string patched = Common + """
                relic "Tally"
                  on card_played(tag:attack) once per battle:
                    gain 10 gold
                  on card_played once per battle:
                    gain 1 gold
                """;
            CardRuntime restored = Load(save, patched);
            PlayStrike(restored);

            // The attack listener fires for the first time; the other has had its turn this battle.
            Assert.Equal(11, Gold(restored));
        }

        [Fact]
        [Trait("Regression", "listener-record-is-positional")]
        public void Identical_on_blocks_each_keep_their_own_limit()
        {
            string twins = Common + """
                relic "Tally"
                  on card_played once per battle:
                    gain 1 gold
                  on card_played once per battle:
                    gain 1 gold
                """;
            string save = UsedTally(twins);

            string patched = Common + """
                relic "Tally"
                  on card_played once per battle:
                    gain 10 gold
                  on card_played once per battle:
                    gain 1 gold
                  on card_played once per battle:
                    gain 1 gold
                """;
            CardRuntime restored = Load(save, patched);
            Assert.Equal(2, Gold(restored));
            PlayFiller(restored);

            Assert.Equal(12, Gold(restored));
        }

        [Fact]
        [Trait("Regression", "listener-record-is-positional")]
        public void The_limit_of_a_removed_listener_is_not_given_to_the_one_now_in_its_place()
        {
            string before = Common + """
                relic "Tally"
                  on card_played once per battle:
                    gain 1 gold
                  on card_played(tag:attack) once per battle:
                    gain 10 gold
                """;
            string save = UsedTally(before);

            string patched = Common + """
                relic "Tally"
                  on card_played(tag:attack) once per battle:
                    gain 10 gold
                """;
            CardRuntime restored = Load(save, patched);
            PlayStrike(restored);

            Assert.Equal(11, Gold(restored));
        }

        [Fact]
        public void A_patch_that_changes_only_the_body_of_a_used_listener_keeps_its_limit()
        {
            string save = UsedTally();

            CardRuntime restored = Load(save, Tally.Replace("gain 1 gold", "gain 2 gold"));
            PlayFiller(restored);

            // Rebalancing what a listener does leaves it the same listener: it has fired this battle.
            Assert.Equal(1, Gold(restored));
        }

        [Fact]
        public void A_patch_that_only_reformats_a_used_listener_keeps_its_limit()
        {
            string save = UsedTally();

            string patched = Common + """
                relic "Tally"
                  # Once a battle, whatever is played.
                  on card_played   once per battle :
                      gain 1 gold
                """;
            CardRuntime restored = Load(save, patched);
            PlayFiller(restored);

            Assert.Equal(1, Gold(restored));
        }

        [Fact]
        public void A_listener_whose_on_line_a_patch_changed_starts_afresh()
        {
            string save = UsedTally();

            // What the listener hears is no longer what it heard, so the saved window does not apply.
            CardRuntime restored = Load(save, Tally.Replace("on card_played once per battle:", "on card_played once per turn:"));
            PlayFiller(restored);

            Assert.Equal(2, Gold(restored));
        }

        [Fact]
        public void A_listener_whose_body_changed_as_it_moved_starts_afresh()
        {
            string save = UsedTally();

            string patched = Common + """
                relic "Tally"
                  on card_exhausted:
                    gain 100 gold
                  on card_played once per battle:
                    gain 2 gold
                """;
            CardRuntime restored = Load(save, patched);
            PlayFiller(restored);

            // Neither place nor statements identify it any more, so its window is not handed on.
            Assert.Equal(3, Gold(restored));
        }

        [Fact]
        public void Saves_without_listener_hashes_are_matched_by_place_as_before()
        {
            // A save made by 0.1.0-preview.2, which recorded the place alone.
            JsonNode json = JsonNode.Parse(UsedTally())!;
            JsonArray limits = json["ListenerLimits"]!.AsArray();
            Assert.Single(limits);
            foreach (JsonNode? limit in limits) limit!.AsObject().Remove("ListenerHash");

            CardRuntime unchanged = Load(json.ToJsonString(), Tally);
            PlayFiller(unchanged);
            Assert.Equal(1, Gold(unchanged));

            // With the place alone, a listener added above takes the limit, as it always did.
            string patched = Common + """
                relic "Tally"
                  on card_played once per battle:
                    gain 10 gold
                  on card_played once per battle:
                    gain 1 gold
                """;
            CardRuntime moved = Load(json.ToJsonString(), patched);
            PlayFiller(moved);
            Assert.Equal(2, Gold(moved));
        }

        [Fact]
        [Trait("Regression", "listener-record-is-positional")]
        public void A_limit_that_a_patch_moved_is_saved_again_at_its_new_place()
        {
            string save = UsedTally();

            string patched = Common + """
                relic "Tally"
                  on card_exhausted:
                    gain 100 gold
                  on card_played once per battle:
                    gain 1 gold
                """;
            CardRuntime restored = Load(save, patched);

            // Saved again, the used limit is recorded against the listener's new place.
            CardRuntime again = Load(Save(restored), patched);
            PlayFiller(again);
            Assert.Equal(1, Gold(again));
        }

        // Timers of `on every` listeners ------------------------------------------------------

        private const string Clockwork = Common + """
            relic "Clockwork"
              on every 2s:
                gain 1 gold
              on every 5s:
                gain 10 gold
            """;

        private static RuntimeOptions RealTime(ulong seed) => new RuntimeOptions { Seed = seed, Clock = new TickClock(10) };

        [Fact]
        [Trait("Regression", "listener-record-is-positional")]
        public void A_patch_that_reorders_on_every_blocks_keeps_each_timer_with_its_listener()
        {
            CardRuntime original = Start(Clockwork, "Clockwork", RealTime(7));
            original.Tick(10);
            string save = Save(original);

            string patched = Common + """
                relic "Clockwork"
                  on every 5s:
                    gain 10 gold
                  on every 2s:
                    gain 1 gold
                """;
            CardRuntime restored = Load(save, patched, RealTime(999));

            restored.Tick(10);   // two seconds in: only the two-second listener is due
            Assert.Equal(1, Gold(restored));

            restored.Tick(30);   // five seconds in: the five-second one has fired once, the other twice
            Assert.Equal(12, Gold(restored));
        }

        [Fact]
        public void A_timer_whose_interval_a_patch_changed_starts_a_fresh_interval()
        {
            CardRuntime original = Start(Clockwork, "Clockwork", RealTime(7));
            original.Tick(10);
            string save = Save(original);

            CardRuntime restored = Load(save, Clockwork.Replace("on every 2s:", "on every 3s:"), RealTime(999));

            restored.Tick(29);   // the new interval counts from the moment the save was made
            Assert.Equal(0, Gold(restored));
            restored.Tick(1);
            Assert.Equal(1, Gold(restored));
        }
    }
}
