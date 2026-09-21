using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// What an inspector can say about one live entity: its numbers before and after the rules act
    /// on them, and every rule it has registered, each with the line it came from.
    /// </summary>
    public sealed class EntityDetailTests
    {
        private const string File = "res://content/test.cantrip";

        private const string Content = @"card ""Ember""
  cost 1
  target enemy
  tags attack, fire
  effect:
    deal 5 to target

status ""Rage""
  tags buff
  stacking intensity
  modify attack: +2 * stacks
  on turn_end(tag:fire) once per turn:
    deal 1 to owner

enemy ""Slime""
  hp 30
  attack 4
  move ""Swipe"":
    deal 4 to player
  pattern cycle Swipe
";

        private static CardRuntime Started(out Entity slime)
        {
            ContentLibrary library = ContentLibrary.FromText(Content, File);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1 });
            runtime.CreatePlayer();
            slime = runtime.SpawnEnemy("Slime");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        [Fact]
        public void An_entity_reports_what_it_is_and_where_it_is()
        {
            CardRuntime runtime = Started(out Entity slime);

            EntityDetail detail = EntityDetail.Of(slime);

            Assert.Equal(slime.Id, detail.Entity.Id);
            Assert.Equal("actor", detail.Entity.Kind);
            Assert.Equal("enemy", detail.Entity.Team);
            Assert.Equal("enemy \"Slime\"", detail.Definition);
            Assert.True(detail.Active);
        }

        [Fact]
        public void A_modified_stat_shows_what_it_started_as_and_what_it_is_now()
        {
            CardRuntime runtime = Started(out Entity slime);
            runtime.ApplyStatus("Rage", slime, 3);

            EntityDetail detail = EntityDetail.Of(slime);

            // The point of carrying both: 4 alone says nothing about the Rage doing the other 6.
            Assert.Equal(4, detail.BaseStats["attack"]);
            Assert.Equal(10, detail.Entity.Stats["attack"]);
            Assert.Equal(30, detail.BaseStats["hp"]);
        }

        [Fact]
        public void A_status_lists_the_rules_it_registered_with_their_lines()
        {
            CardRuntime runtime = Started(out Entity slime);
            Entity rage = runtime.ApplyStatus("Rage", slime, 2)!;

            EntityDetail detail = EntityDetail.Of(rage);

            ModifierView modifier = Assert.Single(detail.Modifiers);
            Assert.Equal("attack", modifier.Channel);
            Assert.Equal("add", modifier.Layer);
            Assert.Equal(File, modifier.File);
            Assert.True(modifier.Line > 0);
            Assert.Contains("modify attack", modifier.Text);

            ListenerView listener = Assert.Single(detail.Listeners);
            Assert.Equal("turn_end", listener.Event);
            Assert.Equal("after", listener.Phase);
            Assert.Equal("turn", listener.Limit);
            Assert.Equal(File, listener.File);
            Assert.Contains("tag:fire", listener.Text);
            Assert.Contains("once per turn", listener.Text);
        }

        [Fact]
        public void A_card_in_the_draw_pile_is_listed_but_not_live()
        {
            CardRuntime runtime = Started(out Entity _);
            Entity card = runtime.AddCard("Ember", Zones.Draw);

            EntityDetail detail = EntityDetail.Of(card);

            Assert.False(detail.Active);
            Assert.Equal("draw", detail.Entity.Zone);
            Assert.Equal(1, detail.BaseStats["cost"]);
            Assert.Empty(detail.Listeners);
        }

        [Fact]
        public void An_entity_with_no_rules_of_its_own_says_so_plainly()
        {
            CardRuntime runtime = Started(out Entity _);

            EntityDetail detail = EntityDetail.Of(runtime.Player!);

            Assert.Empty(detail.Listeners);
            Assert.Empty(detail.Modifiers);
            Assert.Equal(string.Empty, detail.Definition);
            Assert.True(detail.BaseStats.ContainsKey("hp"));
        }

        [Fact]
        public void The_service_lists_what_is_in_play_and_can_be_narrowed()
        {
            CardRuntime runtime = Started(out Entity slime);
            runtime.AddCard("Ember", Zones.Hand);
            var service = new CantripDebugService(runtime);

            Assert.Contains(service.Entities(), e => e.Id == slime.Id);
            Assert.All(service.Entities("hand", null), e => Assert.Equal("hand", e.Zone));
            Assert.All(service.Entities(null, "enemy"), e => Assert.Equal("enemy", e.Team));

            Assert.Equal(slime.Id, service.Detail(slime.Id)!.Entity.Id);
            Assert.Null(service.Detail(99999));
        }
    }
}
