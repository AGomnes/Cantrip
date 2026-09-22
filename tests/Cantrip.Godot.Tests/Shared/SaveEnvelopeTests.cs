using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// What a save file says about the content it was taken against, and what happens when that
    /// content has moved on underneath it.
    /// </summary>
    public sealed class SaveEnvelopeTests
    {
        [Fact]
        public void A_save_taken_against_this_content_is_accepted()
        {
            ContentLibrary library = ViewTestKit.Library();

            SaveEnvelope envelope = SaveEnvelope.Wrap(library.Fingerprint, "{}");
            SaveCheck check = envelope.Check(library.Fingerprint);

            Assert.True(check.Accepted);
            Assert.Equal(SaveRejection.None, check.Reason);
            Assert.Equal(string.Empty, check.Message);
            Assert.Equal("none", check.ReasonName);
            Assert.Equal(SaveEnvelope.CurrentFormat, envelope.Format);
        }

        [Fact]
        public void A_save_from_before_a_definition_was_added_is_refused()
        {
            ContentLibrary before = ViewTestKit.Library();
            SaveEnvelope envelope = SaveEnvelope.Wrap(before.Fingerprint, "{}");

            var after = new ContentLibrary();
            after.LoadText(ViewTestKit.Content + "\ncard \"Newcomer\"\n  cost 1\n  effect:\n    draw 1\n", "res://content/test.cantrip");

            SaveCheck check = envelope.Check(after.Fingerprint);

            Assert.False(check.Accepted);
            Assert.Equal(SaveRejection.ContentChanged, check.Reason);
            Assert.Equal("content_changed", check.ReasonName);
            Assert.Contains(before.Fingerprint, check.Message);
            Assert.Contains(after.Fingerprint, check.Message);
        }

        [Fact]
        public void Rebalancing_a_card_does_not_invalidate_a_save()
        {
            ContentLibrary before = ViewTestKit.Library();
            SaveEnvelope envelope = SaveEnvelope.Wrap(before.Fingerprint, "{}");

            // Same definitions, different numbers: every entity the snapshot names still exists.
            var after = new ContentLibrary();
            after.LoadText(ViewTestKit.Content.Replace("deal 6 to target", "deal 9 to target"), "res://content/test.cantrip");

            Assert.True(envelope.Check(after.Fingerprint).Accepted);
        }

        [Fact]
        public void A_save_from_another_version_of_the_addon_is_refused_before_its_content_is_read()
        {
            ContentLibrary library = ViewTestKit.Library();

            SaveCheck check = new SaveEnvelope(SaveEnvelope.CurrentFormat + 1, library.Fingerprint, "{}").Check(library.Fingerprint);

            Assert.Equal(SaveRejection.WrongFormat, check.Reason);
            Assert.Equal("wrong_format", check.ReasonName);
            Assert.Contains("format " + SaveEnvelope.CurrentFormat, check.Message);
        }

        [Fact]
        public void An_envelope_with_no_game_in_it_is_refused()
        {
            ContentLibrary library = ViewTestKit.Library();

            SaveCheck check = SaveEnvelope.Wrap(library.Fingerprint, string.Empty).Check(library.Fingerprint);

            Assert.Equal(SaveRejection.NoPayload, check.Reason);
            Assert.Equal("no_payload", check.ReasonName);
        }

        [Fact]
        public void A_missing_fingerprint_on_either_side_is_a_mismatch_rather_than_a_crash()
        {
            Assert.Equal(SaveRejection.ContentChanged, SaveEnvelope.Wrap("abc", "{}").Check(null).Reason);
            Assert.Equal(SaveRejection.ContentChanged, new SaveEnvelope(SaveEnvelope.CurrentFormat, null, "{}").Check("abc").Reason);

            // Two libraries with nothing loaded agree, so an empty game can still be saved.
            Assert.True(new SaveEnvelope(SaveEnvelope.CurrentFormat, null, "{}").Check(null).Accepted);
        }

        /// <summary>
        /// The words <c>LoadSave</c> gives script as its <c>reason</c>, pinned one by one, so that
        /// renaming a member of the enum cannot change what a game's script compares against.
        /// </summary>
        [Fact]
        public void Every_rejection_has_its_snake_case_word()
        {
            var words = new Dictionary<SaveRejection, string>
            {
                [SaveRejection.None] = "none",
                [SaveRejection.WrongFormat] = "wrong_format",
                [SaveRejection.NoPayload] = "no_payload",
                [SaveRejection.ContentChanged] = "content_changed",
            };

            Assert.Equal(Enum.GetValues<SaveRejection>().OrderBy(r => r), words.Keys.OrderBy(r => r));
            foreach (KeyValuePair<SaveRejection, string> word in words) Assert.Equal(word.Value, SaveCheck.NameOf(word.Key));
        }

        // The payload --------------------------------------------------------------------------

        [Fact]
        public void A_snapshot_survives_the_round_trip_the_envelope_carries_it_through()
        {
            CardRuntime runtime = ViewTestKit.Runtime(out Entity enemy);
            runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Burn", enemy, 3);

            Assert.True(runtime.CanCapture);
            ulong before = runtime.State.ComputeHash();

            // The envelope treats the payload as opaque text; this is the text the node puts in it.
            SaveEnvelope envelope = SaveEnvelope.Wrap(runtime.Content.Fingerprint, JsonSerializer.Serialize(runtime.Capture()));
            Assert.True(envelope.Check(runtime.Content.Fingerprint).Accepted);

            runtime.Execute("deal 10 to enemy");
            Assert.NotEqual(before, runtime.State.ComputeHash());

            runtime.Restore(JsonSerializer.Deserialize<GameSnapshot>(envelope.Payload)!);

            Assert.Equal(before, runtime.State.ComputeHash());

            // Restoring reuses entity instances, so the id the UI is holding is still this enemy.
            Assert.Same(enemy, runtime.State.Find(enemy.Id));
            Assert.Equal(3, enemy.CounterOf("Burn"));
        }
    }
}
