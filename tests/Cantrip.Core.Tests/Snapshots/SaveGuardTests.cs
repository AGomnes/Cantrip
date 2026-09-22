using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Snapshots
{
    /// <summary>
    /// A save is only taken between actions, and a damaged one is refused before it changes
    /// anything: the two ways a save could otherwise leave a game half done.
    /// </summary>
    public sealed class SaveGuardTests
    {
        private const string Content = """
            enemy "Dummy"
              hp 40

            card "Filler"
              cost 0

            card "Peek"
              cost 0
              effect:
                block 3
                probe
                block 3
            """;

        private static CardRuntime Start()
        {
            var library = new ContentLibrary();
            library.LoadText(Content, "content.cantrip");
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 3 });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            for (int i = 0; i < 8; i++) runtime.AddCard("Filler");
            runtime.StartBattle(shuffle: false, drawOpeningHand: true);
            return runtime;
        }

        [Fact]
        [Trait("Regression", "save-mid-effect")]
        public void A_save_asked_for_from_a_callback_in_the_middle_of_an_effect_is_refused()
        {
            CardRuntime runtime = Start();
            bool? canCapture = null;
            Exception? thrown = null;
            runtime.RegisterVerb("probe", call =>
            {
                // Nothing is queued here: the card's own effect is running, half done.
                canCapture = runtime.CanCapture;
                thrown = Record.Exception(() => runtime.Capture());
            });

            Assert.Equal(PlayResult.Played, runtime.Play(runtime.AddCard("Peek", Zones.Hand)));

            Assert.False(canCapture);
            Assert.IsType<InvalidOperationException>(thrown);
            Assert.Contains("still resolving", thrown!.Message);

            // Between actions, saving works again, with both halves of the effect in it.
            Assert.True(runtime.CanCapture);
            Assert.Equal(6, runtime.Player!.GetInt("block"));
            runtime.Capture();
        }

        public static TheoryData<string, Action<JsonObject>> Damage => new TheoryData<string, Action<JsonObject>>
        {
            { "no entity list", save => save["Entities"] = null },
            { "an entity without stats", save => save["Entities"]![0]!["Stats"] = null },
            { "an entity without tags", save => save["Entities"]![0]!["Tags"] = null },
            { "a null tag", save => save["Entities"]![0]!["Tags"] = new JsonArray((JsonNode?)null) },
            { "an entity without attachments", save => save["Entities"]![0]!["Attached"] = null },
            { "a zone without contents", save => save["Zones"]![0]!["Entities"] = null },
            { "no history", save => save["TurnHistory"] = null },
            { "no listener limits", save => save["ListenerLimits"] = null },
        };

        [Theory]
        [MemberData(nameof(Damage))]
        [Trait("Regression", "damaged-save")]
        public void A_damaged_save_is_refused_before_anything_changes(string damage, Action<JsonObject> apply)
        {
            CardRuntime saved = Start();
            JsonObject save = JsonNode.Parse(JsonSerializer.Serialize(saved.Capture()))!.AsObject();
            apply(save);
            GameSnapshot damaged = save.Deserialize<GameSnapshot>()!;

            CardRuntime runtime = Start();
            runtime.Play(runtime.AddCard("Filler", Zones.Hand));
            string before = JsonSerializer.Serialize(runtime.Capture());

            var error = Assert.Throws<InvalidOperationException>(() => runtime.Restore(damaged));

            Assert.Contains("damaged", error.Message);
            Assert.True(before == JsonSerializer.Serialize(runtime.Capture()), $"{damage}: the game changed");
        }
    }
}
