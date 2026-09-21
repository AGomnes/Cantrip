using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// Hot reload: editing content while a game runs. Entities keep playing; only
    /// what the designer changed changes.
    /// </summary>
    public sealed class HotReloadTests
    {
        private const string File = "content.cantrip";

        private const string Dummy = """
            enemy "Dummy"
              hp 100

            """;

        private static CardRuntime Start(string content, out ContentLibrary library)
        {
            library = new ContentLibrary();
            library.LoadText(content, File);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1 });
            runtime.CreatePlayer();
            return runtime;
        }

        [Fact]
        public void Editing_a_card_changes_what_it_does_in_a_running_game()
        {
            CardRuntime runtime = Start(Dummy + """
                card "Strike"
                  cost 1
                  target enemy
                  effect:
                    deal 6 to target
                """, out ContentLibrary library);

            Entity enemy = runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Strike", Zones.Hand);
            runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.Played, runtime.Play("Strike", enemy));
            Assert.Equal(94, enemy.GetInt("hp"));

            library.LoadText(Dummy + """
                card "Strike"
                  cost 1
                  target enemy
                  effect:
                    deal 9 to target
                """, File);

            CardRuntime.ReloadReport report = runtime.ApplyContentChanges();
            Assert.True(report.Rebound >= 1);
            Assert.Empty(report.Missing);
            Assert.False(report.RulesetChanged);

            Assert.Equal(PlayResult.Played, runtime.Play("Strike", enemy));
            Assert.Equal(85, enemy.GetInt("hp"));
        }

        [Fact]
        public void Listeners_and_modifiers_follow_the_new_definition()
        {
            CardRuntime runtime = Start(Dummy + """
                relic "Gauntlet"
                  modify damage: +2

                relic "Bell"
                  on damaged(source:owner):
                    gain 1 gold
                """, out ContentLibrary library);

            Entity enemy = runtime.SpawnEnemy("Dummy");
            runtime.AddRelic("Gauntlet");
            runtime.AddRelic("Bell");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Execute("deal 5 to enemy");
            Assert.Equal(93, enemy.GetInt("hp"));
            Assert.Equal(1, runtime.Player!.GetInt("gold"));

            library.LoadText(Dummy + """
                relic "Gauntlet"
                  modify damage: +5

                relic "Bell"
                  on damaged(source:owner):
                    gain 10 gold
                """, File);
            runtime.ApplyContentChanges();

            runtime.Execute("deal 5 to enemy");
            Assert.Equal(83, enemy.GetInt("hp"));
            Assert.Equal(11, runtime.Player.GetInt("gold"));
        }

        [Fact]
        public void Stats_the_game_changed_are_kept_while_untouched_ones_update()
        {
            CardRuntime runtime = Start("""
                enemy "Brute"
                  hp 40
                  move "Hit":
                    deal 1 to player
                """, out ContentLibrary library);

            Entity brute = runtime.SpawnEnemy("Brute");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 10 to enemy");
            Assert.Equal(30, brute.GetInt("hp"));
            Assert.Equal(40, brute.GetInt("max_hp"));

            library.LoadText("""
                enemy "Brute"
                  hp 50
                  armor 3
                  move "Hit":
                    deal 1 to player
                """, File);
            runtime.ApplyContentChanges();

            Assert.Equal(30, brute.GetInt("hp"));      // mid-fight damage is not healed by an edit
            Assert.Equal(50, brute.GetInt("max_hp"));  // still at the old printed value, so it updates
            Assert.Equal(3, brute.GetInt("armor"));    // a brand new stat appears
        }

        [Fact]
        public void Tags_from_content_update_while_runtime_tags_survive()
        {
            CardRuntime runtime = Start("""
                card "Jab"
                  cost 0
                  tags attack, quick
                  effect:
                    block 1
                """, out ContentLibrary library);

            Entity card = runtime.AddCard("Jab", Zones.Hand);
            card.AddTag("marked");

            library.LoadText("""
                card "Jab"
                  cost 0
                  tags attack, fire
                  effect:
                    block 1
                """, File);
            runtime.ApplyContentChanges();

            Assert.True(card.HasTag("attack"));
            Assert.True(card.HasTag("fire"));
            Assert.False(card.HasTag("quick"));
            Assert.True(card.HasTag("marked"));
        }

        [Fact]
        public void A_definition_that_disappears_is_reported_and_its_entities_keep_working()
        {
            CardRuntime runtime = Start(Dummy + """
                card "Strike"
                  cost 1
                  target enemy
                  effect:
                    deal 6 to target
                """, out ContentLibrary library);

            Entity enemy = runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Strike", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            library.LoadText(Dummy + """
                card "Defend"
                  cost 1
                  effect:
                    block 5
                """, File);

            CardRuntime.ReloadReport report = runtime.ApplyContentChanges();
            Assert.Contains("card \"Strike\"", report.Missing);

            Assert.Equal(PlayResult.Played, runtime.Play("Strike", enemy));
            Assert.Equal(94, enemy.GetInt("hp"));
        }

        [Fact]
        public void A_ruleset_edit_is_reported_because_a_running_game_keeps_its_rules()
        {
            CardRuntime runtime = Start("""
                ruleset
                  hand_size 5
                """, out ContentLibrary library);

            Assert.False(runtime.ApplyContentChanges().RulesetChanged);

            library.LoadText("""
                ruleset
                  hand_size 7
                """, File);

            CardRuntime.ReloadReport report = runtime.ApplyContentChanges();
            Assert.True(report.RulesetChanged);
            Assert.Equal(5, runtime.State.Rules.HandSize);
        }

        [Fact]
        public void Statuses_rebind_and_keep_their_stacks()
        {
            CardRuntime runtime = Start(Dummy + """
                status "Venom"
                  stacking intensity
                  on turn_end:
                    deal stacks to owner, ignore block
                """, out ContentLibrary library);

            Entity enemy = runtime.SpawnEnemy("Dummy");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Venom", enemy, 3);

            library.LoadText(Dummy + """
                status "Venom"
                  stacking intensity
                  on turn_end:
                    deal 2 * stacks to owner, ignore block
                """, File);
            runtime.ApplyContentChanges();

            Entity venom = enemy.Attached.Single();
            Assert.Equal(3, venom.GetInt("stacks"));

            runtime.EndTurn();
            Assert.Equal(94, enemy.GetInt("hp"));
        }
    }
}
