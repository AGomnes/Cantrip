using System.Linq;
using Cantrip.Content;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Docs
{
    /// <summary>
    /// Rules that load cleanly and then do something other than a content author expects. Each
    /// test pins the behaviour that docs/language.md, docs/writing-content.md and the sharp edges
    /// in docs/coverage.md describe, so the docs and the engine cannot drift apart.
    /// </summary>
    public sealed class ContentTrapTests
    {
        private const string Brute = """
            enemy Brute
              hp 100
              move Smash:
                deal 25 to player

            """;

        /// <summary>
        /// The limit is used up when the listener fires, whatever its body does, so a condition in
        /// an <c>if</c> spends it on the first hit; in the filter, it waits for the hit that matters.
        /// </summary>
        [Fact]
        public void Once_per_battle_is_spent_when_the_listener_fires_even_if_its_body_does_nothing() => DocsDsl.AssertPasses(Brute + """
            relic "Charm in the body"
              on owner.damaged once per battle:
                if owner.hp <= owner.max_hp / 2:
                  heal 10

            relic "Charm in the filter"
              on owner.damaged(owner.hp <= owner.max_hp / 2) once per battle:
                heal 10

            test "with the condition in an if, the first hit spends the limit"
              enemy Brute
              relic "Charm in the body"
              end turn
              expect player.hp == 55
              end turn
              expect player.hp == 30

            test "with the condition in the filter, the limit waits for it"
              enemy Brute
              relic "Charm in the filter"
              end turn
              expect player.hp == 55
              end turn
              expect player.hp == 40
            """);

        /// <summary>
        /// A scope is matched against the event's target. <c>card_played</c>'s target is whoever the
        /// card was played at, so <c>on owner.card_played</c> hears only cards played at the holder.
        /// </summary>
        [Fact]
        public void An_owner_scope_on_card_played_hears_cards_played_at_the_holder_not_by_it() => DocsDsl.AssertPasses("""
            relic Scoped
              heard 0
              on owner.card_played:
                heard += 1

            relic Unscoped
              heard 0
              on card_played:
                heard += 1

            relic "By Source"
              heard 0
              on card_played(source:owner):
                heard += 1

            card Strike
              cost 0
              target enemy
              effect:
                deal 1 to target

            card Defend
              cost 0
              effect:
                block 1

            card Bandage
              cost 0
              target self
              effect:
                heal 1

            test "only the card played at the player reaches the owner-scoped relic"
              enemy hp 20
              relic Scoped
              relic Unscoped
              relic "By Source"
              play Strike on enemy
              play Defend
              play Bandage
              expect count(relics where name:Scoped and heard == 1) == 1
              expect count(relics where name:Unscoped and heard == 3) == 1
              expect count(relics where name:"By Source" and heard == 3) == 1
            """);

        /// <summary>
        /// A status is read and written by name on the entity it is on: <c>owner.Weak</c> inside a
        /// status, <c>target.Weak</c> in a card. There is no name <c>host</c>.
        /// </summary>
        [Fact]
        public void A_status_counter_is_read_and_written_through_the_entity_it_is_on() => DocsDsl.AssertPasses("""
            status Weak
              tags debuff
              stacking duration
              modify damage: x0.75

            status Relief
              on owner.turn_end:
                owner.Weak -1

            card Sap
              cost 0
              target enemy
              effect:
                apply Weak 3 to target
                target.Weak -1

            card Curse
              cost 0
              target enemy
              effect:
                target.Weak +2

            test "target.Weak -1 lowers the counter"
              enemy hp 20
              play Sap on enemy
              expect enemy.Weak == 2

            test "writing to a status the target lacks applies it"
              enemy hp 20
              play Curse on enemy
              expect enemy.Weak == 2

            test "owner.Weak inside a status reads its host's Weak"
              enemy hp 20
              apply Weak 3 to player
              apply Relief 1 to player
              end turn
              expect player.Weak == 1
            """);

        [Fact]
        public void Host_is_not_a_name()
        {
            ContentLibrary content = ContentLibrary.FromText("""
                status Weak
                  stacking duration

                card Sap
                  cost 0
                  target enemy
                  effect:
                    host.Weak -1

                test "host is unknown"
                  enemy hp 20
                  play Sap on enemy
                """, "host.cantrip");

            DslTestResult result = new DslTestRunner(content).RunAll().Single();
            Assert.False(result.Passed);
            Assert.StartsWith("runtime error: Unknown name `host`", result.Failure);
        }

        /// <summary>
        /// <c>damage</c> is what the modifier's anchor deals and <c>damage_taken</c> what it
        /// receives, so a Vulnerable written with <c>modify damage: x1.5</c> makes its host hit
        /// harder instead of taking more.
        /// </summary>
        [Fact]
        public void Damage_is_what_the_host_deals_and_damage_taken_what_it_receives() => DocsDsl.AssertPasses(Brute + """
            status Vulnerable
              tags debuff
              stacking duration
              modify damage_taken: x1.5

            status Mistaken
              tags debuff
              stacking duration
              modify damage: x1.5

            test "damage_taken: the host takes half as much again"
              enemy Brute
              apply Vulnerable 2 to enemy
              deal 10 to enemy
              expect enemy.hp == 85
              end turn
              expect player.hp == 55

            test "damage: the host deals half as much again"
              enemy Brute
              apply Mistaken 2 to enemy
              deal 10 to enemy
              expect enemy.hp == 90
              end turn
              expect player.hp == 43
            """);
    }
}
