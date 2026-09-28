using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Battle
{
    /// <summary>
    /// The <c>targetable</c> channel binds everything that is pointed at somebody: a card's target,
    /// an enemy's move, and the <c>attack</c> verb.
    /// </summary>
    /// <remarks>
    /// It used to bind only the first of those, so a taunt stopped a card being aimed at a minion
    /// and then the minion attacked past it, and an enemy's move went to the player as though the
    /// taunt were not there. A taunt and a stealth now mean one thing wherever something is aimed.
    /// Area and random effects are deliberately untouched: a taunt constrains what something may be
    /// pointed at, not what a blast reaches.
    /// </remarks>
    public sealed class TargetableReachTests
    {
        private const string Board = """
            status "Taunt"
              stacking none
              modify targetable of allies where source:enemies, not it.has(Taunt): set 0

            status "Stealth"
              stacking none
              modify targetable: set 0

            actor "Footman"
              hp 10
              attack 2

            actor "Guard"
              hp 12
              attack 1

            card "Muster"
              cost 0
              effect:
                create Footman

            card "Blast"
              cost 0
              effect:
                deal 2 to all enemies

            card "Charge First"
              cost 0
              effect:
                attack enemies.first with allies.last

            card "Charge Last"
              cost 0
              effect:
                attack enemies.last with allies.last

            """;

        [Fact]
        [Trait("Regression", "targetable-ignored-by-attack-and-moves")]
        public void An_attack_cannot_reach_past_a_taunt() => Passes(Board + """
            enemy "Ogre"
              hp 20
              attack 3

            enemy "Wall"
              hp 20
              attack 0

            test "a minion may only swing at what a taunt leaves available"
              enemy Ogre hp 20 attack 3
              enemy Wall hp 20 attack 0
              hand Muster, "Charge First", "Charge Last"
              player energy 9
              play Muster
              apply Taunt 1 to enemies.last
              play "Charge First"
              expect enemies.first.hp == 20
              play "Charge Last"
              expect enemies.last.hp == 18
            """);

        [Fact]
        [Trait("Regression", "targetable-ignored-by-attack-and-moves")]
        public void An_attack_cannot_reach_a_stealthed_enemy_but_a_blast_still_does() => Passes(Board + """
            enemy "Sneak"
              hp 20
              attack 1

            test "stealth stops the swing and not the blast"
              enemy Sneak hp 20 attack 1
              hand Muster, "Charge First", Blast
              player energy 9
              play Muster
              apply Stealth 1 to enemies.first
              play "Charge First"
              expect enemies.first.hp == 20
              play Blast
              expect enemies.first.hp == 18
            """);

        /// <summary>
        /// The other half: an enemy's move is pointed at somebody too, so a taunting minion takes
        /// the hit the player would have taken.
        /// </summary>
        [Fact]
        [Trait("Regression", "targetable-ignored-by-attack-and-moves")]
        public void An_enemy_move_goes_to_whoever_a_taunt_leaves_available() => Passes(Board + """
            enemy "Ogre"
              hp 20
              move "Smash":
                deal 5 to target

            test "the Guard with Taunt takes the smash"
              player hp 40
              enemy Ogre hp 20
              hand Muster
              player energy 9
              play Muster
              apply Taunt 1 to allies.last
              end turn
              expect player.hp == 40
              expect allies.last.hp == 5
            """);

        /// <summary>Without a taunt nothing moves: the move still lands on the player.</summary>
        [Fact]
        public void An_enemy_move_still_hits_the_player_when_nothing_says_otherwise() => Passes(Board + """
            enemy "Ogre"
              hp 20
              move "Smash":
                deal 5 to target

            test "the smash lands on the player"
              player hp 40
              enemy Ogre hp 20
              hand Muster
              player energy 9
              play Muster
              end turn
              expect player.hp == 35
              expect allies.last.hp == 10
            """);

        // Helpers ----------------------------------------------------------------------------

        private static void Passes(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "targetable.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());

            IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }
    }
}
