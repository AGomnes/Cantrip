using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Testing;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// <c>copy</c>: duplicating something that is in the game, as it stands, rather than making a
    /// fresh one from its definition.
    /// </summary>
    /// <remarks>
    /// The two decisions that carry the verb are the side and the zone. A copy takes its side and
    /// owner from the <em>original</em>, so copying an enemy's minion gives the enemy a second one;
    /// and it is placed by <em>kind</em>, where a new one would go, never in the zone the original
    /// happens to sit in.
    /// </remarks>
    public sealed class CopyVerbTests
    {
        private const string Sharpening = """
            status "Sharpened"
              tags buff
              stacking intensity
              modify damage: +3

            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Whetstone"
              cost 0
              effect:
                choose 1 from hand where tag:attack as picked
                apply Sharpened 1 to picked

            card "Dual Wield"
              cost 1
              effect:
                choose 1 from hand where tag:attack or tag:power as picked
                copy picked into hand

            """;

        /// <summary>
        /// The whole feature in two numbers: the copy is sharpened, and it hits for 9. With
        /// <c>create picked</c> in its place the same fixture gives 0 and 6.
        /// </summary>
        [Fact]
        public void A_copy_carries_what_the_original_gained_this_battle() => Passes(Sharpening + """
            test "Dual Wield copies the sharpened Strike, not a fresh one"
              hand Strike, Whetstone, "Dual Wield"
              enemy hp 50
              player energy 9
              play Whetstone
              play "Dual Wield"
              expect hand.count == 2
              expect hand.last.name == "Strike"
              expect hand.last.Sharpened == 1
              play hand.last on enemy
              expect enemy.hp == 41
            """);

        /// <summary>
        /// The side comes from the original, not from whoever is copying. An <c>actor</c> summoned by
        /// an enemy is an enemy; a player card that copies it must not be handed the minion.
        /// </summary>
        [Fact]
        public void A_copy_of_an_enemy_is_an_enemy_whatever_the_declaration_says() => Passes("""
            actor "Imp"
              hp 10

            enemy "Summoner"
              hp 30
              move "Call":
                create Imp

            card "Mirror"
              cost 0
              effect:
                copy (enemies where name:Imp).first

            test "a copy of an enemy is an enemy"
              enemy Summoner hp 30
              hand Mirror
              player energy 9
              end turn
              expect enemies.count == 2
              play Mirror
              expect enemies.count == 3
              expect allies.count == 1
            """);

        /// <summary>
        /// Inheriting the original's zone is the attractive wrong answer: it would put a second live
        /// power into <c>powers</c> that nobody played, and a copy of an exhausted card where nothing
        /// can reach it. One rule — where a new one would go — covers every zone, including the ones
        /// a game adds later.
        /// </summary>
        [Fact]
        public void A_copy_is_placed_by_kind_never_by_the_originals_zone() => Passes("""
            card "Focus"
              cost 1
              tags power
              effect:
                gain 1 strength

            card "Ember"
              cost 0
              target enemy
              tags exhaust
              effect:
                deal 1 to target

            card "Echo"
              cost 0
              effect:
                copy (powers).first

            card "Recall"
              cost 0
              effect:
                copy (exhaust).first

            card "Rake"
              cost 0
              effect:
                copy (discard).first

            test "a copy of an active power goes to hand, and powers is unchanged"
              hand Focus, Echo
              enemy hp 50
              player energy 9
              play Focus
              expect powers.count == 1
              play Echo
              expect powers.count == 1
              expect hand.count == 1
              expect hand.first.name == "Focus"

            test "a copy of an exhausted card goes to hand, and the exhaust pile is unchanged"
              hand Ember, Recall
              enemy hp 50
              player energy 9
              play Ember on enemy
              expect exhaust.count == 1
              play Recall
              expect exhaust.count == 1
              expect hand.count == 1
              expect hand.first.name == "Ember"

            test "a copy of a discarded card goes to hand"
              hand Ember, Rake
              enemy hp 50
              player energy 9
              discard hand.first
              expect discard.count == 1
              play Rake
              expect hand.count == 1
              expect hand.first.name == "Ember"
            """);

        /// <summary>
        /// A copy is a snapshot of a state, not a new application: no <c>status_applied</c> is raised
        /// and <c>immune</c> is never asked, so a poison carried by the original comes across even
        /// when the copy would resist being poisoned.
        /// </summary>
        [Fact]
        public void Statuses_come_across_silently_and_past_immunity() => Passes("""
            status "Poison"
              stacking intensity

            status "Cleansed"
              stacking intensity
              immune Poison

            relic "Watcher"
              counter 0
              on status_applied:
                counter +1

            enemy "Ogre"
              hp 30

            test "a copied status raises nothing and skips immune"
              enemy Ogre hp 30 Poison 4
              relic Watcher
              player energy 9
              expect relics.first.counter == 0
              apply Cleansed 1 to enemies.first
              expect relics.first.counter == 1
              copy enemies.first
              expect enemies.count == 2
              expect enemies.last.Poison == 4
              expect enemies.last.Cleansed == 1
              expect relics.first.counter == 1
            """);

        /// <summary>
        /// <c>created</c> fires for a copy, carrying <c>copy_of</c>, which is also what makes the
        /// original count as involved — so <c>on created(self)</c> on the original hears its own
        /// copying. That is deliberate, and it is in the sharp edges.
        /// </summary>
        [Fact]
        public void A_copy_raises_created_and_the_original_is_involved() => Passes("""
            enemy "Ogre"
              hp 30

            relic "Tally"
              counter 0
              on created:
                counter +1

            relic "Reader"
              counter 0
              on created:
                if event.copy_of != none:
                  counter += 1

            actor "Totem"
              hp 5
              spotted 0
              on created(self):
                spotted += 1

            test "a copy raises created once"
              enemy Ogre hp 30
              relic Tally
              player energy 9
              copy enemies.first
              expect relics.first.counter == 1
              expect copied.count == 1
              expect copied.first.name == "Ogre"

            test "the event carries copy_of, and a plain create carries none"
              enemy Ogre hp 30
              relic Reader
              player energy 9
              create Ogre
              expect enemies.count == 2
              expect relics.first.counter == 0
              copy enemies.first
              expect enemies.count == 3
              expect relics.first.counter == 1

            test "the original hears its own copying, because copy_of involves it"
              enemy hp 50
              player energy 9
              create Totem
              let original = created.first
              expect allies.count == 2
              expect original.spotted == 1
              copy original
              expect allies.count == 3
              # The copy hears its own creation, as anything created does...
              expect copied.first.spotted == 2
              # ...and so does the original, which is what copy_of is for.
              expect original.spotted == 2
            """);

        /// <summary>
        /// Hp is not restored, and neither is anything else: a rule that reset <c>hp</c> could not say
        /// why it left <c>energy</c>, <c>charge</c> or <c>fuel</c> alone. Content that wants a fresh
        /// one writes one visible line.
        /// </summary>
        [Fact]
        public void A_wounded_original_is_copied_wounded_and_content_can_restore_it() => Passes("""
            enemy "Ogre"
              hp 30

            test "the copy arrives at the hp the original has"
              enemy Ogre hp 30
              player energy 9
              deal 11 to enemies.first
              copy enemies.first
              expect enemies.last.hp == 19
              copied.hp = copied.max_hp
              expect enemies.last.hp == 30
            """);

        /// <summary>
        /// <c>copied</c> is always a list, and a one-element list takes member access, so both
        /// <c>copied.hp</c> and <c>copied.first.hp</c> read the copy.
        /// </summary>
        [Fact]
        public void Copied_is_a_list_that_a_single_copy_still_reads_through() => Passes("""
            enemy "Ogre"
              hp 30

            test "one copy, read either way"
              enemy Ogre hp 30
              player energy 9
              copy enemies.first
              expect copied.count == 1
              expect copied.hp == 30
              expect copied.first.hp == 30

            test "a count makes that many"
              enemy Ogre hp 30
              player energy 9
              copy enemies.first 2
              expect enemies.count == 3
              expect copied.count == 2

            test "a group with nothing in it copies nothing, and says so"
              enemy hp 50
              player energy 9
              copy (hand where tag:attack)
              expect copied.count == 0
              expect hand.count == 0
            """);

        /// <summary>A copy of a copy is ordinary: the second takes the first's statuses as they stand.</summary>
        [Fact]
        public void A_copy_of_a_copy_takes_the_first_copys_state() => Passes("""
            status "Poison"
              stacking intensity

            enemy "Ogre"
              hp 30

            test "the second copy is of the first, not of the original"
              enemy Ogre hp 30 Poison 2
              player energy 9
              copy enemies.first
              apply Poison 5 to copied
              expect enemies.last.Poison == 7
              copy enemies.last
              expect enemies.count == 3
              expect enemies.last.Poison == 7
            """);

        /// <summary>
        /// <c>create</c> ignores <c>max_hand_size</c> and so does <c>copy</c>: one rule for where a new
        /// thing goes, rather than a second rule invented for the new verb.
        /// </summary>
        [Fact]
        public void A_copy_into_a_full_hand_is_kept_exactly_as_create_keeps_one() => Passes("""
            card "Filler"
              cost 0
              effect:
                draw 0

            card "Twin"
              cost 0
              effect:
                copy hand.first 5 into hand

            test "copy fills past the hand limit, as create does"
              hand Filler
              enemy hp 50
              player energy 9
              create Filler 10 into hand
              expect hand.count == 11
              play Twin
              expect hand.count == 16
            """);

        /// <summary>
        /// A copy is a new combatant: its <c>once per battle</c> is unused, and an actor rolls a fresh
        /// intent, exactly as a created one does.
        /// </summary>
        [Fact]
        public void A_copy_arrives_with_its_limits_unspent_and_an_intent_of_its_own() => Passes("""
            enemy "Bellower"
              hp 30
              on turn_start once per battle:
                block 5
              move "Roar":
                deal 3 to player

            test "the copy has its own once-per-battle and its own intent"
              enemy Bellower hp 30
              player energy 9
              end turn
              expect enemies.first.block == 5
              copy enemies.first
              expect enemies.last.intent == "Roar"
              end turn
              expect enemies.last.block == 5
            """);

        /// <summary>
        /// <c>until</c> records a tag, an attachment or a stat delta; none of those is a thing coming
        /// into the game, so neither <c>create</c> nor <c>copy</c> is taken back. Consistency with
        /// <c>create</c> beats cleverness here.
        /// </summary>
        [Fact]
        public void A_copy_made_inside_until_is_not_taken_back_any_more_than_a_create_is() => Passes("""
            card "Shiv"
              cost 0
              target enemy
              tags attack
              effect:
                deal 4 to target

            card "Fleeting"
              cost 0
              effect:
                until turn_end:
                  copy hand.first into hand

            test "the copy is still there after the deadline"
              hand Shiv, Fleeting
              enemy hp 50
              player energy 9
              play Fleeting
              expect hand.count == 2
              expect cards.count == 3
              end turn
              expect cards.count == 3
            """);

        /// <summary>
        /// Owner comes from the original, so an enemy's card copied by an enemy's own effect goes to
        /// the enemy's hand and never touches the player's.
        /// </summary>
        [Fact]
        public void An_enemys_card_is_copied_into_the_enemys_hand()
        {
            CardRuntime runtime = Create("""
                card "Slash"
                  cost 0
                  target enemy
                  tags attack
                  effect:
                    deal 7 to target

                enemy "Duellist"
                  hp 40
                  move Wait:
                    block 1
                """);
            Entity duellist = runtime.SpawnEnemy("Duellist");
            Start(runtime);

            Entity slash = runtime.AddCard("Slash", Zones.Hand, owner: duellist);
            runtime.Execute("copy card", slash);

            Assert.Equal(2, runtime.State.ZoneOf(duellist, Zones.Hand).Count);
            Assert.Empty(runtime.State.ZoneOf(runtime.Player, Zones.Hand));

            Entity copy = runtime.State.ZoneOf(duellist, Zones.Hand)[1];
            Assert.Equal(duellist, copy.Owner);
            Assert.Equal(duellist, copy.Controller);
        }

        // Refusals ---------------------------------------------------------------------------

        [Fact]
        public void A_definition_is_refused_and_names_create()
        {
            string failure = FirstFailure("""
                card "Strike"
                  cost 1
                  target enemy
                  effect:
                    deal 6 to target

                test "a definition is not something in the game"
                  enemy hp 10
                  player energy 9
                  copy Strike
                """);

            Assert.Contains("duplicates something that is in the game", failure);
            Assert.Contains("create Strike", failure);
        }

        /// <summary>
        /// A quoted name is text, not a definition, and text is not something in the game either.
        /// Without this it would evaluate to no entities at all and copy nothing, silently — which is
        /// exactly the failure the language exists to refuse.
        /// </summary>
        [Fact]
        public void A_quoted_name_is_refused_the_same_way_and_quoted_back()
        {
            const string Cards = """
                card "Fire Bolt"
                  cost 0
                  target enemy
                  effect:
                    deal 4 to target

                """;

            string failure = FirstFailure(Cards + """
                card "Mirror"
                  cost 0
                  effect:
                    copy "Fire Bolt"

                test "a quoted name is content too"
                  enemy hp 50
                  hand Mirror
                  player energy 9
                  play Mirror
                """);
            Assert.Contains("duplicates something that is in the game", failure);
            Assert.Contains("create \"Fire Bolt\"", failure);

            string unknown = FirstFailure(Cards + """
                card "Mirror"
                  cost 0
                  effect:
                    copy "Frie Bolt"

                test "a quoted name that is not content"
                  enemy hp 50
                  hand Mirror
                  player energy 9
                  play Mirror
                """);
            Assert.Contains("nothing named `Frie Bolt` is defined", unknown);
            Assert.Contains("Did you mean `Fire Bolt`?", unknown);
        }

        [Fact]
        public void The_player_is_refused_because_nothing_in_content_describes_it()
        {
            string failure = FirstFailure("""
                test "the player was not made from content"
                  enemy hp 10
                  player energy 9
                  copy player
                """);

            Assert.Contains("`Player` was not made from content", failure);
        }

        [Fact]
        public void A_status_is_refused_and_names_apply()
        {
            string failure = FirstFailure("""
                status "Poison"
                  stacking intensity

                test "a status belongs to a host, not to a zone"
                  enemy hp 10 Poison 3
                  player energy 9
                  copy (enemies.first.statuses).first
                """);

            Assert.Contains("`Poison` is a status", failure);
            Assert.Contains("apply Poison 3 to", failure);
        }

        // Saves ------------------------------------------------------------------------------

        /// <summary>
        /// A copy is an ordinary entity, so a snapshot holds it without the save format moving: the
        /// hash matches after a round trip and the game can still be captured afterwards.
        /// </summary>
        [Fact]
        public void A_game_with_a_copy_in_play_round_trips_through_a_snapshot()
        {
            CardRuntime runtime = Create("""
                status "Sharpened"
                  stacking intensity

                card "Strike"
                  cost 1
                  target enemy
                  tags attack
                  effect:
                    deal 6 to target
                """);
            Entity enemy = Enemy(runtime, 50);
            Start(runtime);

            Entity strike = runtime.AddCard("Strike", Zones.Hand);
            runtime.Execute("apply Sharpened 2 to card", strike);
            runtime.Execute("copy hand.first into hand");

            Assert.Equal(2, runtime.State.ZoneOf(runtime.Player, Zones.Hand).Count);
            Entity copy = runtime.State.ZoneOf(runtime.Player, Zones.Hand)[1];
            Assert.Equal(2, copy.StacksOf("Sharpened"));

            ulong before = runtime.State.ComputeHash();
            Assert.True(runtime.CanCapture);
            GameSnapshot snapshot = runtime.Capture();
            Assert.Equal(GameSnapshot.CurrentFormat, snapshot.FormatVersion);

            runtime.Execute("deal 5 to enemy", null, enemy);
            runtime.Restore(snapshot);

            Assert.Equal(before, runtime.State.ComputeHash());
            Assert.True(runtime.CanCapture);
            Assert.Equal(2, runtime.State.Find(copy.Id)!.StacksOf("Sharpened"));
        }

        [Fact]
        [Trait("Regression", "copy-revives-the-dead")]
        public void A_dead_actor_is_skipped_the_way_a_removed_one_is()
        {
            // `kill` marks an actor dead without touching its hp, so copying one would put it back
            // on the board alive and whole, and `on killed: copy event.target` would never stop.
            Passes("""
                card "Necromancy"
                  cost 0
                  target enemy
                  effect:
                    let doomed = target
                    kill doomed
                    copy doomed

                test "a killed enemy cannot be copied back"
                  enemy "Ghoul" hp 30
                  play "Necromancy" on enemy
                  expect count(enemies) == 0
                """);
        }

        // Helpers ----------------------------------------------------------------------------

        private static void Passes(string dsl)
        {
            ContentLibrary content = Load(dsl);
            IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }

        private static string FirstFailure(string dsl)
        {
            DslTestResult result = new DslTestRunner(Load(dsl)).RunAll().First();
            Assert.False(result.Passed, "expected the test to be refused, but it passed");
            return result.Failure!;
        }

        private static ContentLibrary Load(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "copy-verb.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }
    }
}
