using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Snapshots
{
    /// <summary>
    /// The save this promise is actually about: one written by a published Cantrip, kept in
    /// <c>Fixtures/</c> as that release wrote it, restored by the build under test.
    /// </summary>
    /// <remarks>
    /// The two files beside this one are frozen. <c>0.1.0-preview.5.save.json</c> came out of a
    /// build of the <c>v0.1.0-preview.5</c> tag, playing the game the numbers below describe
    /// against <c>0.1.0-preview.5.content.cantrip</c>, which is that tag's
    /// <c>samples/basic/content.cantrip</c> copied here so that nothing else in the repository can
    /// change what this test means. Neither file is ever edited: a later release adds its own pair
    /// beside them, and this file gets a case for it. If this test fails, a save that some player
    /// has on disk no longer loads, which is the thing docs/stability.md says cannot happen after
    /// 1.0 — so the change that broke it is the thing to reconsider, not this test.
    /// <para>
    /// The save is taken mid-battle with two kinds of work waiting in it: a <c>next turn:</c> block
    /// from <c>Prepare</c>, which is found again by the address and hash the save records, and an
    /// <c>until turn_end:</c> from <c>Flex</c>, which holds two Strength to give back. Both are
    /// recorded under the names format 1 gave them, so this is also what pins the rename.
    /// </para>
    /// </remarks>
    public sealed class PublishedSaveTests
    {
        /// <summary>The state hash the preview.5 build printed for the game it saved.</summary>
        private const ulong Saved = 15834000141062751246UL;

        /// <summary>And the hash it printed after playing one more turn from there.</summary>
        private const ulong OneTurnOn = 6355761473111257813UL;

        [Fact]
        public void A_save_written_by_0_1_0_preview_5_restores_into_this_build()
        {
            GameSnapshot save = Fixture("0.1.0-preview.5.save.json");

            // As that release wrote it: format 1, with none of what a save says about itself now.
            Assert.Equal(1, save.FormatVersion);
            Assert.Equal(0, save.MinimumReader);
            Assert.Equal(string.Empty, save.WrittenBy);
            Assert.Equal(string.Empty, save.RngGenerator);

            CardRuntime restored = Fresh();
            restored.Restore(save);

            Assert.Equal(Saved, restored.State.ComputeHash());
            Assert.Equal(49, restored.Player!.GetInt("hp"));
            Assert.Equal(2, restored.Player.StacksOf("Strength"));
            Assert.Equal(5, restored.State.ZoneOf(restored.Player, Zones.Hand).Count);
            Assert.Equal(5, restored.State.ZoneOf(restored.Player, Zones.Draw).Count);
            Assert.Equal(5, restored.State.Actors(Team.Enemy).Single().GetInt("hp"));
        }

        [Fact]
        public void The_work_waiting_in_a_0_1_0_preview_5_save_is_found_again_and_runs()
        {
            GameSnapshot save = Fixture("0.1.0-preview.5.save.json");

            // Written under the names format 1 gave them, and read under the names that mean them.
            Assert.Equal(2, save.Scheduled.Count);
            Assert.Equal("card:Prepare/effect/0.body", save.Scheduled.Single(s => s.BlockAddress != null).BlockAddress);
            Assert.Equal(BuiltinEvents.TurnEnd, save.Scheduled.Single(s => s.UntilEvent != null).UntilEvent);

            CardRuntime restored = Fresh();
            restored.Restore(save);
            Assert.Equal(2, restored.State.Scheduled.Count);

            restored.EndTurn();

            // The `until` gave its Strength back, the `next turn:` drew, and the whole game is the
            // one preview.5 itself played from this save.
            Assert.Equal(0, restored.Player!.StacksOf("Strength"));
            Assert.Equal(OneTurnOn, restored.State.ComputeHash());
        }

        /// <summary>
        /// The upgrade step ran, so what is in memory after the restore is in this build's format,
        /// and saving the game again writes a save that says so.
        /// </summary>
        [Fact]
        public void A_0_1_0_preview_5_save_restored_and_saved_again_comes_back_in_this_format()
        {
            GameSnapshot save = Fixture("0.1.0-preview.5.save.json");
            Assert.Equal(1, save.FormatVersion);

            CardRuntime restored = Fresh();
            restored.Restore(save);
            Assert.Equal(GameSnapshot.CurrentFormat, save.FormatVersion);

            GameSnapshot again = restored.Capture();
            Assert.Equal(GameSnapshot.CurrentFormat, again.FormatVersion);
            Assert.Equal(GameSnapshot.CurrentMinimumReader, again.MinimumReader);
            Assert.Equal(GameSnapshot.CurrentWriter, again.WrittenBy);
            Assert.Equal(GameSnapshot.CurrentRng, again.RngGenerator);

            CardRuntime twice = Fresh();
            twice.Restore(JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(again))!);
            Assert.Equal(Saved, twice.State.ComputeHash());
        }

        private static CardRuntime Fresh() =>
            new CardRuntime(ContentLibrary.FromText(Text("0.1.0-preview.5.content.cantrip"), "content.cantrip"), new RuntimeOptions { Seed = 999 });

        private static GameSnapshot Fixture(string name) =>
            JsonSerializer.Deserialize<GameSnapshot>(Text(name), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException(name + " is empty.");

        private static string Text(string name) =>
            File.ReadAllText(Path.Combine(SnapshotScenario.RepositoryRoot(), "tests", "Cantrip.Core.Tests", "Snapshots", "Fixtures", name));
    }
}
