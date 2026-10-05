#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// <see cref="Entity.Tags"/> and <see cref="Entity.Attached"/> used to hand out the entity's own
    /// collections, typed as read-only. Casting one back and mutating it changed the game without
    /// telling the state, so the modifier cache, which is keyed on tags and dropped only when the
    /// state says it moved, went on serving the old number.
    /// </summary>
    public sealed class LiveCollectionTests
    {
        private const string Content = """
            relic "Kindling"
              modify damage where tag:fire: +5

            card "Bolt"
              cost 0
              target enemy
              effect:
                deal 4 to target

            status "Mark"
              tags debuff

            enemy "Dummy"
              hp 60
            """;

        private static CardRuntime Started(out Entity player, out Entity enemy)
        {
            CardRuntime runtime = CardRuntime.FromText(Content, new RuntimeOptions { Seed = 1 });
            player = runtime.CreatePlayer();
            enemy = runtime.SpawnEnemy("Dummy");
            runtime.AddRelic("Kindling");
            runtime.AddCard("Bolt", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        [Fact]
        public void Tags_are_not_the_entitys_own_set()
        {
            CardRuntime runtime = Started(out _, out _);
            Entity bolt = runtime.State.ZoneOf(runtime.Player, Zones.Hand).Single();

            Assert.IsNotType<HashSet<string>>(bolt.Tags);
            Assert.Throws<NotSupportedException>(() => ((ICollection<string>)bolt.Tags).Add("fire"));
            Assert.Throws<NotSupportedException>(() => ((ICollection<string>)bolt.Tags).Clear());
        }

        [Fact]
        public void Attachments_are_not_the_entitys_own_list()
        {
            CardRuntime runtime = Started(out Entity player, out _);
            runtime.ApplyStatus("Mark", player);
            Entity mark = player.Attached.Single();

            Assert.IsNotType<List<Entity>>(player.Attached);
            Assert.Throws<NotSupportedException>(() => ((ICollection<Entity>)player.Attached).Add(mark));
            Assert.Throws<NotSupportedException>(() => ((IList<Entity>)player.Attached).RemoveAt(0));
        }

        /// <summary>
        /// The door that is left: <see cref="Entity.AddTag"/>, which tells the state, so the next
        /// read of a tag-scoped modifier is computed afresh rather than served from the cache.
        /// </summary>
        [Fact]
        public void A_tag_added_the_supported_way_reaches_the_modifier_that_reads_it()
        {
            CardRuntime runtime = Started(out _, out Entity enemy);
            Entity bolt = runtime.State.ZoneOf(runtime.Player, Zones.Hand).Single();

            bolt.AddTag("fire");
            Assert.Contains("fire", bolt.Tags);

            Assert.Equal(ActionResult.Played, runtime.Play(bolt, enemy));
            Assert.Equal(51, enemy.GetInt("hp"));   // 60 - (4 + 5)
        }

        [Fact]
        public void Reading_a_view_still_gives_the_live_contents()
        {
            CardRuntime runtime = Started(out Entity player, out _);
            Assert.Empty(player.Attached);

            runtime.ApplyStatus("Mark", player);

            // The view is not a copy taken once; it reads the entity as it is now.
            Assert.Single(player.Attached);
            Assert.Equal("Mark", player.Attached[0].Name);
            Assert.Equal(new[] { "Mark" }, player.Attached.Select(a => a.Name).ToArray());
        }
    }
}
