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
    /// <c>transform</c>: replacing what something <em>is</em> while keeping who it is.
    /// </summary>
    /// <remarks>
    /// The workaround it replaces is <c>destroy</c> plus <c>create</c>, which loses the id, the slot
    /// and — for a generic <c>actor</c> — the side. What survives here is the whole point: anything
    /// holding the entity, from a test's <c>enemy2</c> to a C# reference a game caches, still holds
    /// the same object, now wearing the new name.
    /// </remarks>
    public sealed class TransformVerbTests
    {
        private const string Polymorph = """
            status "Buffed"
              stacking intensity

            enemy "Ogre"
              hp 30
              tags big
              on battle_start once per battle:
                apply Buffed 3 to self
              on self.died:
                deal 7 to player
              move "Smash":
                deal 9 to player

            enemy "Sheepling"
              hp 1
              tags meek
              move "Baa":
                deal 1 to player

            card "Polymorph"
              cost 4
              target enemy
              tags spell
              effect:
                transform target into Sheepling

            """;

        /// <summary>
        /// <c>enemy2</c> is the test's own binding to that entity, so <c>expect enemy2.name ==
        /// "Sheepling"</c> is the identity claim stated as a test. The <c>destroy</c>+<c>create</c>
        /// version of this fixture passes with <c>enemy2.removed</c> and the replacement at the back.
        /// </summary>
        [Fact]
        public void A_transform_keeps_the_seat_and_the_identity_and_skips_the_death_rattle() => Passes(Polymorph + """
            test "Polymorph keeps the seat and the identity"
              enemy Ogre hp 30
              enemy Ogre hp 30
              enemy Ogre hp 30
              hand Polymorph
              player hp 80
              player energy 9
              expect enemy2.position == 1
              expect enemy2.Buffed == 3
              play Polymorph on enemy2
              expect enemies.count == 3
              expect enemy2.name == "Sheepling"
              expect enemy2.position == 1
              expect enemy2.hp == 1
              expect enemy2.Buffed == 0
              expect enemy2.has(tag:meek)
              expect not enemy2.has(tag:big)
              expect enemy2.intent == "Baa"
              expect player.hp == 80
            """);

        /// <summary>
        /// One event, raised once. Statuses leave silently, because a variable number of cancellable
        /// events — each able to destroy the host half way through — is not something content could
        /// reason about. <c>destroy</c> already sheds its attachments the same way.
        /// </summary>
        [Fact]
        public void Statuses_go_silently_so_a_status_removed_listener_cannot_reach_a_half_transformed_host() => Passes(Polymorph + """
            relic "Sabotage"
              on status_removed:
                destroy event.target

            relic "Witness"
              counter 0
              on transformed:
                counter += 1

            test "the host survives, and transformed fires exactly once"
              enemy Ogre hp 30 Buffed 3
              relic Sabotage
              relic Witness
              hand Polymorph
              player energy 9
              play Polymorph on enemy
              expect enemies.count == 1
              expect enemy1.name == "Sheepling"
              expect not enemy1.removed
              expect relics.last.counter == 1
            """);

        [Fact]
        public void A_before_listener_can_cancel_it_outright() => Passes(Polymorph + """
            relic "Veto"
              on before_transformed:
                cancel

            test "cancel stops it dead: name, hp and statuses unchanged"
              enemy Ogre hp 30
              relic Veto
              hand Polymorph
              player energy 9
              play Polymorph on enemy
              expect enemy1.name == "Ogre"
              expect enemy1.hp == 30
              expect enemy1.Buffed == 3
            """);

        /// <summary>
        /// The committed work checks again that the entity is still here, and marks the event
        /// cancelled when it is not — so the after phase never claims a transform that did not happen.
        /// </summary>
        [Fact]
        public void A_before_listener_that_destroys_the_target_leaves_nothing_half_transformed() => Passes(Polymorph + """
            relic "Assassin"
              on before_transformed:
                destroy event.target

            relic "Witness"
              counter 0
              on transformed:
                counter += 1

            test "the after phase never runs, and Become never runs on a removed entity"
              enemy Ogre hp 30
              relic Assassin
              relic Witness
              hand Polymorph
              player energy 9
              play Polymorph on enemy
              expect enemy1.removed
              expect enemy1.name == "Ogre"
              expect relics.last.counter == 0
            """);

        /// <summary>
        /// Both declarations are actors, and the side is kept, so one <c>actor</c> serves both sides.
        /// With <c>create</c> the same thing needs two declarations, because a generic actor joins
        /// whoever made it.
        /// </summary>
        [Fact]
        public void An_enemy_that_becomes_a_generic_actor_stays_an_enemy() => Passes("""
            actor "Sheep"
              hp 4
              move "Baa":
                deal 1 to player

            enemy "Ogre"
              hp 30
              move "Smash":
                deal 9 to player

            card "Shear"
              cost 0
              target enemy
              effect:
                transform target into Sheep

            test "one actor declaration serves both sides"
              enemy Ogre hp 30
              hand Shear
              player energy 9
              play Shear on enemy
              expect enemies.count == 1
              expect allies.count == 1
              expect enemy1.name == "Sheep"
              expect enemy1.intent == "Baa"
              end turn
              expect player.hp == 79
            """);

        /// <summary>
        /// Wounds go, because the new thing is a different creature. Content that wants them carried
        /// says so in three visible lines, which also generalises to any stat a game wants kept.
        /// </summary>
        [Fact]
        public void Wounds_are_gone_and_the_three_line_idiom_carries_them() => Passes("""
            actor "Man"
              hp 20
              max_hp 20

            actor "Beast"
              hp 30
              max_hp 30

            card "Rage"
              cost 0
              target ally
              effect:
                let wounds = target.max_hp - target.hp
                transform target into Beast
                target.hp = target.max_hp - wounds

            card "Plain"
              cost 0
              target ally
              effect:
                transform target into Beast

            test "the wounds carry when content carries them"
              enemy hp 50
              hand Rage
              player energy 9
              create Man
              deal 8 to allies.last
              expect allies.last.hp == 12
              play Rage on allies.last
              expect allies.last.name == "Beast"
              expect allies.last.hp == 22

            test "without those lines the new thing arrives whole"
              enemy hp 50
              hand Plain
              player energy 9
              create Man
              deal 8 to allies.last
              play Plain on allies.last
              expect allies.last.hp == 30
            """);

        /// <summary>
        /// Transforming into what it already is is not a no-op but a reset: printed stats back,
        /// statuses gone, spent limits fresh. Saying "no-op" would be a lie about what happened.
        /// </summary>
        [Fact]
        public void Transforming_into_what_it_already_is_resets_it() => Passes("""
            status "Buffed"
              stacking intensity

            enemy "Ogre"
              hp 30
              on battle_start once per battle:
                block 5
              move "Smash":
                deal 9 to player

            test "printed stats back, statuses gone, once per battle fresh"
              enemy Ogre hp 30 Buffed 3
              player energy 9
              expect enemy1.Buffed == 3
              expect enemy1.block == 5
              deal 11 to enemies.first, ignore block
              expect enemy1.hp == 19
              transform enemies.first into Ogre
              expect enemy1.hp == 30
              expect enemy1.Buffed == 0
              expect enemy1.block == 0
            """);

        /// <summary>A card keeps its index in hand, and a card mid-play is filed by the tags it has now.</summary>
        [Fact]
        public void A_card_keeps_its_place_and_a_resolving_card_is_filed_by_its_new_tags() => Passes("""
            card "Alpha"
              cost 0
              effect:
                draw 0

            card "Beta"
              cost 0
              effect:
                block 3

            card "Gamma"
              cost 0
              effect:
                heal 1

            card "PolyMid"
              cost 0
              effect:
                transform hand.first into Gamma

            card "Ember"
              cost 0
              target enemy
              tags attack
              effect:
                transform self into Lasting
                deal 4 to target

            card "Lasting"
              cost 0
              tags power
              effect:
                draw 0

            test "hand [Alpha, Beta, PolyMid] reads [Gamma, Beta] afterwards"
              hand Alpha, Beta, PolyMid
              enemy hp 50
              player energy 9
              play PolyMid
              expect hand.count == 2
              expect hand.first.name == "Gamma"
              expect hand.last.name == "Beta"

            test "the running effect finishes, and the new tags decide where the card goes"
              hand Ember
              enemy hp 50
              player energy 9
              play Ember on enemy
              expect enemy.hp == 46
              expect powers.count == 1
              expect powers.first.name == "Lasting"
              expect discard.count == 0
            """);

        /// <summary>
        /// The old thing's <c>next turn:</c> plan goes with the definition that made it: an Ogre that
        /// scheduled 7 damage and then became a Sheep deals the Sheep's 1.
        /// </summary>
        [Fact]
        public void The_old_things_scheduled_work_is_dropped() => Passes("""
            actor "Sheep"
              hp 4
              move "Baa":
                deal 1 to player

            enemy "Planner"
              hp 30
              move "Wind up":
                next turn:
                  deal 7 to player

            card "Shear"
              cost 0
              target enemy
              effect:
                transform target into Sheep

            test "the plan is dropped with the definition"
              enemy Planner hp 30
              hand Shear
              player hp 80
              player energy 9
              end turn
              expect player.hp == 80
              play Shear on enemy
              end turn
              expect player.hp == 79
            """);

        /// <summary><c>into</c> takes a definition, and a <c>discover</c> binding is one.</summary>
        [Fact]
        public void Into_takes_a_definition_value_such_as_a_discover_binding() => Passes(Polymorph + """
            card "Curse of Shapes"
              cost 0
              target enemy
              effect:
                discover 1 enemies where name:Sheepling as found
                transform target into found

            test "a definition bound by discover works"
              enemy Ogre hp 30
              hand "Curse of Shapes"
              player energy 9
              play "Curse of Shapes" on enemy
              expect enemy1.name == "Sheepling"
            """);

        /// <summary>A group is iterated over a snapshot, and a member that has left it is skipped.</summary>
        [Fact]
        public void A_group_transforms_together_and_skips_what_left_it_mid_loop() => Passes(Polymorph + """
            card "Mass Poly"
              cost 0
              effect:
                transform enemies into Sheepling

            card "Cull"
              cost 0
              effect:
                kill enemies.first
                transform enemies into Sheepling

            test "every enemy becomes a Sheepling"
              enemy Ogre hp 30
              enemy Ogre hp 30
              hand "Mass Poly"
              player energy 9
              play "Mass Poly"
              expect enemies.count == 2
              expect enemy1.name == "Sheepling"
              expect enemy2.name == "Sheepling"

            test "one that died on the line before is simply not there"
              enemy Ogre hp 30
              enemy Ogre hp 30
              hand Cull
              player energy 9
              play Cull
              expect enemies.count == 1
              expect enemies.first.name == "Sheepling"
            """);

        // Refusals ---------------------------------------------------------------------------

        /// <summary>
        /// <c>transform a into b</c>, where <c>b</c> is something in the game, parses today and gives
        /// the <em>printed</em> stats of whatever <c>b</c> is. Two readings of one clause, told apart
        /// by whether a name happens to be a local, is a silent wrong answer: it is refused by name.
        /// </summary>
        [Fact]
        public void Into_an_entity_is_refused_by_name()
        {
            string failure = FirstFailure(Polymorph + """
                card "Faceless"
                  cost 0
                  target enemy
                  effect:
                    transform target into enemies.last

                test "into an entity"
                  enemy Ogre hp 30
                  enemy Ogre hp 30
                  hand Faceless
                  player energy 9
                  play Faceless on enemy
                """);

            Assert.Contains("`into` takes the name of something written in content", failure);
            Assert.Contains("`enemies.last` is something in the game", failure);
        }

        [Fact]
        public void A_different_kind_is_refused_and_names_both()
        {
            string failure = FirstFailure(Polymorph + """
                card "Wound"
                  cost 0
                  tags unplayable

                card "Curse It"
                  cost 0
                  target enemy
                  effect:
                    transform target into Wound

                test "an enemy cannot become a card"
                  enemy Ogre hp 30
                  hand "Curse It"
                  player energy 9
                  play "Curse It" on enemy
                """);

            Assert.Contains("an enemy cannot become the card `Wound`", failure);
            Assert.Contains("only something of the same kind can stand in its place", failure);
        }

        [Fact]
        public void The_player_is_refused_because_nothing_in_content_describes_it()
        {
            string failure = FirstFailure(Polymorph + """
                test "the player is not made from content"
                  enemy Ogre hp 30
                  player energy 9
                  transform player into Sheepling
                """);

            Assert.Contains("`Player` was not made from content", failure);
        }

        /// <summary>
        /// The runtime refuses it as well as the linter, which is what catches a content verb holding
        /// one, called from inside an <c>until</c>.
        /// </summary>
        [Fact]
        public void Inside_until_is_refused_at_runtime_as_well_as_at_lint()
        {
            string failure = FirstFailure(Polymorph + """
                card "Temporary"
                  cost 0
                  target enemy
                  effect:
                    until turn_end:
                      transform target into Sheepling

                test "until cannot hold one"
                  enemy Ogre hp 30
                  hand Temporary
                  player energy 9
                  play Temporary on enemy
                """);

            Assert.Contains("cannot be undone, so an `until` block cannot hold one", failure);
        }

        [Fact]
        public void A_content_verb_holding_one_is_refused_when_it_is_called_from_inside_until()
        {
            string failure = FirstFailure(Polymorph + """
                verb sheepify(who):
                  transform who into Sheepling

                card "Temporary"
                  cost 0
                  target enemy
                  effect:
                    until turn_end:
                      sheepify target

                test "the runtime catches what the linter cannot see"
                  enemy Ogre hp 30
                  hand Temporary
                  player energy 9
                  play Temporary on enemy
                """);

            Assert.Contains("cannot be undone, so an `until` block cannot hold one", failure);
        }

        // Saves and rollback -----------------------------------------------------------------

        /// <summary>
        /// The precondition <c>transform</c> breaks, and the reason its fix belongs in this change.
        /// <see cref="CardRuntime.Attempt"/> restores a snapshot every time a deferred choice is
        /// raised, and <c>GameState.Restore</c> used to reuse an existing instance only when the name
        /// matched as well as the id — which silently assumed a name never changes. A game with a
        /// real chooser would be handed a stale, removed object while the runtime held a new one.
        /// </summary>
        [Fact]
        public void A_transform_followed_by_a_choice_keeps_the_caller_holding_the_same_object()
        {
            ContentLibrary content = Load("""
                enemy "Ogre"
                  hp 30
                  move "Smash":
                    deal 9 to player

                enemy "Sheepling"
                  hp 1
                  move "Baa":
                    deal 1 to player

                card "Filler"
                  cost 0
                  effect:
                    draw 0

                card "Shape and Sift"
                  cost 0
                  target enemy
                  effect:
                    transform target into Sheepling
                    choose 1 from hand as picked
                    exhaust picked
                """);

            var chooser = new DeferredChooser();
            var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 1, Chooser = chooser });
            runtime.CreatePlayer();
            Entity ogre = runtime.SpawnEnemy("Ogre");
            int ogreId = ogre.Id;
            runtime.AddCard("Filler", Zones.Hand);
            runtime.AddCard("Filler", Zones.Hand);
            Entity card = runtime.AddCard("Shape and Sift", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Assert.Equal(PlayResult.ChoicePending, runtime.Play(card, ogre));

            // The action rolled back, so the game is where it was — and the caller's reference is
            // still the game's entity, not an abandoned instance beside it.
            Assert.Same(ogre, runtime.State.Find(ogreId));
            Assert.Equal("Ogre", ogre.Name);
            Assert.False(ogre.IsRemoved);

            Entity picked = runtime.State.ZoneOf(runtime.Player, Zones.Hand).First(c => c.Name == "Filler");
            Assert.Equal(PlayResult.Played, runtime.Answer(picked.Id));

            Assert.Same(ogre, runtime.State.Find(ogreId));
            Assert.Equal("Sheepling", ogre.Name);
            Assert.Equal(1, ogre.GetInt("hp"));
        }

        /// <summary>
        /// A transformed entity round-trips for free: the snapshot already stored <c>Name</c>,
        /// <c>DefinitionKind</c> and <c>DefinitionName</c> separately, so the save format does not move.
        /// </summary>
        [Fact]
        public void A_transformed_entity_round_trips_through_a_snapshot()
        {
            CardRuntime runtime = Create("""
                enemy "Ogre"
                  hp 30
                  move "Smash":
                    deal 9 to player

                enemy "Sheepling"
                  hp 1
                  move "Baa":
                    deal 1 to player
                """);
            Entity ogre = runtime.SpawnEnemy("Ogre");
            Start(runtime);

            runtime.Execute("transform enemies.first into Sheepling");
            Assert.Equal("Sheepling", ogre.Name);

            ulong before = runtime.State.ComputeHash();
            Assert.True(runtime.CanCapture);
            GameSnapshot snapshot = runtime.Capture();
            Assert.Equal(GameSnapshot.CurrentFormat, snapshot.FormatVersion);

            runtime.Execute("transform enemies.first into Ogre");
            Assert.Equal("Ogre", ogre.Name);
            Assert.NotEqual(before, runtime.State.ComputeHash());

            runtime.Restore(snapshot);

            Assert.Equal(before, runtime.State.ComputeHash());
            Assert.True(runtime.CanCapture);
            Assert.Same(ogre, runtime.State.Find(ogre.Id));
            Assert.Equal("Sheepling", ogre.Name);
            Assert.Equal("Sheepling", ogre.Definition!.Name);
        }

        /// <summary>
        /// An <c>until</c> revert aimed at a stat the entity no longer has is pruned, so a buff given
        /// to the Ogre is never taken off the Sheep.
        /// </summary>
        [Fact]
        public void An_until_revert_aimed_at_what_was_transformed_is_pruned() => Passes("""
            enemy "Ogre"
              hp 30
              might 5
              move "Smash":
                deal 9 to player

            enemy "Sheepling"
              hp 1
              move "Baa":
                deal 1 to player

            card "Empower"
              cost 0
              target enemy
              effect:
                until turn_end:
                  target.might += 4

            card "Polymorph"
              cost 0
              target enemy
              effect:
                transform target into Sheepling

            test "the revert has nothing left to take back"
              enemy Ogre hp 30
              hand Empower, Polymorph
              player energy 9
              play Empower on enemy
              expect enemy1.might == 9
              play Polymorph on enemy
              expect enemy1.name == "Sheepling"
              expect enemy1.might == 0
              end turn
              expect enemy1.might == 0
            """);

        /// <summary>
        /// The new thing starts at the beginning of its own pattern. Without the reset, a Drummer
        /// that had already taken its first turn would arrive as a Piper part way through the Piper's
        /// cycle — a move the player was never shown, chosen by the old definition's bookkeeping.
        /// </summary>
        [Fact]
        public void The_pattern_starts_again_in_the_new_definition() => Passes("""
            enemy "Drummer"
              hp 30
              pattern cycle Tap, Bang
              move "Tap":
                deal 1 to player
              move "Bang":
                deal 2 to player

            enemy "Piper"
              hp 20
              pattern cycle Toot, Blast
              move "Toot":
                deal 3 to player
              move "Blast":
                deal 30 to player

            card "Repipe"
              cost 0
              target enemy
              effect:
                transform target into Piper

            test "the Piper starts at the top of its own cycle"
              enemy Drummer hp 30
              enemy Drummer hp 30
              hand Repipe
              player hp 80
              player energy 9
              expect enemy1.intent == "Tap"
              play Repipe on enemy1
              expect enemy1.name == "Piper"
              expect enemy1.intent == "Toot"
              end turn
              # 3 from the Piper's first move and 1 from the other Drummer's.
              expect player.hp == 76
            """);

        /// <summary>
        /// A definition in the subject slot is refused, as <c>copy</c> and <c>play</c> refuse one.
        /// <c>transform Ogre into Sheepling</c> reads as if it changed every Ogre; a definition holds
        /// no entities, so without the refusal the whole statement does nothing and says nothing.
        /// </summary>
        [Fact]
        public void A_definition_where_something_in_the_game_is_meant_is_refused()
        {
            string failure = FirstFailure(Polymorph + """
                card "Wrong"
                  cost 0
                  effect:
                    transform Ogre into Sheepling

                test "a definition is not something in the game"
                  enemy Ogre hp 30
                  enemy Ogre hp 30
                  hand Wrong
                  player energy 9
                  play Wrong
                """);

            Assert.Contains("`transform` changes something that is in the game", failure);
            Assert.Contains("`Ogre` is content", failure);
            Assert.Contains("transform target into Sheepling", failure);
        }

        /// <summary>A quoted name is text rather than a definition, and lands in the same place.</summary>
        [Fact]
        public void A_quoted_name_in_the_subject_slot_is_refused_the_same_way()
        {
            string failure = FirstFailure(Polymorph + """
                card "Wrong"
                  cost 0
                  effect:
                    transform "Ogre" into Sheepling

                test "a quoted name is content too"
                  enemy Ogre hp 30
                  enemy Ogre hp 30
                  hand Wrong
                  player energy 9
                  play Wrong
                """);

            Assert.Contains("`Ogre` is content", failure);
        }

        // Helpers ----------------------------------------------------------------------------

        private static void Passes(string dsl)
        {
            IReadOnlyList<DslTestResult> results = new DslTestRunner(Load(dsl)).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }

        private static string FirstFailure(string dsl)
        {
            DslTestResult result = new DslTestRunner(Load(dsl)).RunAll().Last();
            Assert.False(result.Passed, "expected the test to be refused, but it passed");
            return result.Failure!;
        }

        private static ContentLibrary Load(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "transform-verb.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }
    }
}
