using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Small pieces of API that engine adapters need: telling a UI when saving is legal, knowing
    /// whether content changed under a save file, and being honest about a trimmed trace.
    /// </summary>
    public sealed class AdapterApiTests
    {
        private const string Card = """
            card "Strike"
              cost 1
              target enemy
              effect:
                deal 6 to target
            """;

        [Fact]
        public void A_fingerprint_survives_reloading_the_same_content()
        {
            var library = new ContentLibrary();
            library.LoadText(Card, "cards.cantrip");
            string first = library.Fingerprint;

            library.LoadText(Card, "cards.cantrip");
            Assert.Equal(first, library.Fingerprint);

            // A second library built the same way agrees, so a save can carry the value.
            Assert.Equal(first, ContentLibrary.FromText(Card, "cards.cantrip").Fingerprint);
            Assert.Matches("^[0-9a-f]{16}$", first);
        }

        [Fact]
        public void A_fingerprint_changes_when_the_content_does()
        {
            var library = new ContentLibrary();
            library.LoadText(Card, "cards.cantrip");
            string before = library.Fingerprint;

            library.LoadText(Card + """

                card "Defend"
                  cost 1
                  effect:
                    block 5
                """, "cards.cantrip");

            Assert.NotEqual(before, library.Fingerprint);
        }

        [Fact]
        public void A_fingerprint_ignores_edits_that_add_no_definitions()
        {
            var library = new ContentLibrary();
            library.LoadText(Card, "cards.cantrip");
            string before = library.Fingerprint;

            // Same definitions, different numbers: the save still fits, so the fingerprint holds.
            library.LoadText(Card.Replace("deal 6", "deal 9"), "cards.cantrip");
            Assert.Equal(before, library.Fingerprint);
        }

        [Fact]
        public void A_trimmed_trace_says_how_much_it_dropped()
        {
            var log = new TraceLog { Enabled = true, Capacity = 2 };

            log.Record(0, "action", "one");
            log.Record(0, "action", "two");
            Assert.Equal(0, log.Dropped);

            log.Record(0, "action", "three");
            log.Record(0, "action", "four");

            Assert.Equal(2, log.Entries.Count);
            Assert.Equal(2, log.Dropped);
            Assert.Equal("four", log.Entries[1].Description);

            log.Clear();
            Assert.Equal(0, log.Dropped);
        }

        [Fact]
        public void A_runtime_says_when_it_can_be_captured()
        {
            CardRuntime runtime = CardRuntime.FromText(Card + """

                enemy "Dummy"
                  hp 20
                """);
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.True(runtime.CanCapture);
            runtime.Capture();   // the promise CanCapture makes
        }
    }
}
