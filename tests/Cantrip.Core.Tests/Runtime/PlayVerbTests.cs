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
    /// <c>play</c>: playing a card out of a pile, with its cost and its triggers, without the player
    /// choosing it.
    /// </summary>
    /// <remarks>
    /// <c>replay</c> resolves a card's effect again for nothing, which is close and is not the same
    /// thing: the card never leaves its pile, nothing is paid, and no <c>card_played</c> is raised.
    /// This is a real play, and everything that counts a play counts it.
    /// </remarks>
    public sealed class PlayVerbTests
    {
        private const string Havoc = """
            card "Ember"
              cost 2
              target enemy
              tags attack
              effect:
                deal 9 to target

            relic "Tally"
              counter 0
              on card_played:
                counter +1

            card "Havoc"
              cost 1
              tags skill, exhaust
              effect:
                play draw.first, free
                if played == none:
                  discard draw.first
                else:
                  exhaust played

            """;

        /// <summary>
        /// Four of these numbers are what <c>replay draw.first on enemy</c> gets wrong: the Ember
        /// never leaves the pile, only Havoc is exhausted, the Ember was never played, and the energy
        /// is right only by accident.
        /// </summary>
        [Fact]
        public void Havoc_plays_the_top_card_for_nothing_and_it_counts_as_played() => Passes(Havoc + """
            test "the top card is played, and it counts"
              relic Tally
              deck Ember
              hand Havoc
              enemy hp 50
              play Havoc
              expect enemy.hp == 41
              expect player.energy == 2
              expect draw.count == 0
              expect exhaust.count == 2
              expect relics.first.counter == 2
            """);

        /// <summary>
        /// A refusal the rules allow is not an error. "Play the top card of your draw pile" must not
        /// crash the first time the top card is a Curse, so <c>played == none</c> is how content asks.
        /// </summary>
        [Fact]
        public void Every_refusal_the_rules_allow_binds_none_rather_than_failing() => Passes("""
            card "Ember"
              cost 2
              target enemy
              tags attack
              effect:
                deal 9 to target

            card "Curse"
              cost 0
              tags unplayable

            card "Probe"
              cost 0
              effect:
                play draw.first
                if played == none:
                  deal 1 to player

            test "an empty draw pile"
              hand Probe
              enemy hp 50
              player hp 80
              player energy 9
              play Probe
              expect player.hp == 79

            test "a cost more than the payer has"
              deck Ember
              hand Probe
              enemy hp 50
              player hp 80
              player max_energy 1
              play Probe
              expect player.hp == 79
              expect enemy.hp == 50
              expect draw.count == 1

            test "an unplayable card"
              deck Curse
              hand Probe
              enemy hp 50
              player hp 80
              player energy 9
              play Probe
              expect player.hp == 79
              expect draw.count == 1

            test "no legal target, because every enemy is gone"
              deck Ember
              hand Probe
              player hp 80
              player energy 9
              play Probe
              expect player.hp == 79
              expect draw.count == 1
            """);

        /// <summary>
        /// <c>, free</c> changes what is paid, never what the card sees. Binding <c>x = 0</c> instead
        /// would make "play the top card of your draw pile" do nothing at all, silently, for every X
        /// card in the game.
        /// </summary>
        [Fact]
        public void Free_on_an_x_cost_card_binds_x_to_what_the_payer_has_and_spends_nothing() => Passes("""
            card "Xplode"
              cost x
              target enemy
              effect:
                deal x * 3 to target

            relic "Ledger"
              counter 0
              on card_played:
                counter += event.amount

            card "TopFree"
              cost 0
              effect:
                play draw.first, free

            card "TopPaid"
              cost 0
              effect:
                play draw.first

            test "played free, x is what the payer has and nothing is spent"
              relic Ledger
              deck Xplode
              hand TopFree
              enemy hp 400
              player energy 9
              play TopFree
              expect enemy.hp == 373
              expect player.energy == 9
              # The event's amount is what was actually paid, so "per energy spent" stays honest.
              expect relics.first.counter == 0

            test "played paid, an X card spends everything, as from hand"
              deck Xplode
              hand TopPaid
              enemy hp 400
              player energy 9
              play TopPaid
              expect enemy.hp == 373
              expect player.energy == 0
            """);

        [Fact]
        public void A_card_played_from_the_discard_pile_pays_and_returns_there() => Passes("""
            card "Ember"
              cost 2
              target enemy
              tags attack
              effect:
                deal 9 to target

            card "Dredge"
              cost 0
              effect:
                play discard.first

            test "it pays, it hits, and it goes back to the discard pile"
              discard_pile Ember
              hand Dredge
              enemy hp 40
              player energy 9
              play Dredge
              expect player.energy == 7
              expect enemy.hp == 31
              expect discard.count == 2
            """);

        [Fact]
        public void A_card_already_in_play_or_powers_cannot_be_played_again() => Passes("""
            card "Twice"
              cost 0
              effect:
                play hand.first
                play hand.first

            test "the second play finds the card mid-play and does nothing"
              hand Twice
              enemy hp 50
              player energy 9
              play Twice
              expect discard.count == 1
            """);

        // Chains, depth and ordering ---------------------------------------------------------

        /// <summary>
        /// A nested play extends its caller's chain, so <c>once per chain</c> counts one chain across
        /// the whole cascade. Without the threading, <c>PlayCore</c> would start a fresh chain at
        /// every play boundary and this would run away.
        /// </summary>
        [Fact]
        public void A_play_inside_a_listener_plays_exactly_one_extra_card() => Passes("""
            card "Blast"
              cost 0
              target enemy
              tags attack, spark
              effect:
                deal 3 to target

            relic "Cascade"
              on card_played(tag:spark):
                play hand.first

            test "one extra card, not a cascade"
              relic Cascade
              hand Blast, Blast, Blast, Blast
              enemy hp 500
              player energy 9
              play hand.first on enemy
              expect enemy.hp == 494
              expect hand.count == 2
            """);

        /// <summary>
        /// The guard pinned rather than incidental: with a fresh chain per play — what
        /// <c>PlayCore</c> does when nothing threads the caller's through — the same content runs the
        /// whole pile out instead of one extra card.
        /// </summary>
        [Fact]
        public void With_a_fresh_chain_per_play_the_same_content_runs_away()
        {
            CardRuntime runtime = Create("""
                card "Blast"
                  cost 0
                  target enemy
                  tags attack, spark
                  effect:
                    deal 3 to target

                relic "Cascade"
                  on card_played(tag:spark):
                    cascade hand.first
                """);

            // The same shape as the `play` verb, minus the one line that threads the chain:
            // CardRuntime.Play starts a fresh one, as PlayCore does when nothing passes it a chain.
            runtime.RegisterVerb("cascade", call =>
            {
                Entity? card = call.Argument(0).AsEntities().FirstOrDefault();
                if (card != null) runtime.Play(card, null);
            });

            Entity enemy = Enemy(runtime, 500);
            runtime.AddRelic("Cascade");
            var hand = Enumerable.Range(0, 4).Select(_ => runtime.AddCard("Blast", Zones.Hand)).ToList();
            Start(runtime);

            runtime.Play(hand[0], enemy);

            // Every card in hand went, because each play began a chain of its own and the listener
            // was never already in it.
            Assert.Empty(runtime.State.ZoneOf(runtime.Player, Zones.Hand));
            Assert.Equal(488, Hp(enemy));
        }

        /// <summary>
        /// Direct recursion stops at <c>max_call_depth</c> with a message that names what happened —
        /// not a stack overflow, and not the 100,000-step budget, which would say nothing useful.
        /// </summary>
        [Fact]
        public void A_card_that_creates_and_plays_itself_stops_at_the_call_depth()
        {
            string failure = FirstFailure("""
                card "Selfish"
                  cost 0
                  effect:
                    create Selfish into draw
                    play draw.first

                test "a card playing itself"
                  hand Selfish
                  enemy hp 50
                  player energy 9
                  play Selfish
                """);

            Assert.Contains("cards have played each other more than 64 deep", failure);
            Assert.Contains("is a card playing itself?", failure);
            Assert.DoesNotContain("Step limit", failure);
        }

        /// <summary>
        /// Only the outermost play drains. A nested one that drained would resolve the outer effect's
        /// already-queued after-listeners in the middle of that effect, which nothing else in the
        /// language does.
        /// </summary>
        [Fact]
        public void A_nested_play_does_not_drain_the_outer_effects_queued_triggers() => Passes("""
            relic "Bookkeeper"
              on drawn:
                player.mark += 1

            card "Idle"
              cost 0
              effect:
                block 0

            card "Outer"
              cost 0
              effect:
                draw 1
                play draw.first
                player.seen = player.mark

            test "the trigger resolves after the outer effect, not inside the nested play"
              relic Bookkeeper
              deck Idle, Idle
              hand Outer
              enemy hp 50
              player energy 9
              play Outer
              expect player.seen == 0
              expect player.mark == 1
            """);

        // The name -----------------------------------------------------------------------------

        /// <summary>
        /// The test verb and the rule verb are told apart by <em>where the line is written</em>. A
        /// content verb takes its context from its caller, so a dynamic check reads the same call two
        /// ways depending on who started it — and gets this case wrong.
        /// </summary>
        [Fact]
        public void A_content_verb_holding_play_is_the_rules_verb_from_a_test_line_and_from_a_card() => Passes("""
            card "Ember"
              cost 0
              target enemy
              tags attack
              effect:
                deal 9 to target

            verb cascade():
              play draw.first

            card "Caller"
              cost 0
              effect:
                cascade

            test "called straight from a test line"
              deck Ember
              enemy hp 50
              player energy 9
              cascade
              expect enemy.hp == 41
              expect draw.count == 0

            test "called from a card, in the same run"
              deck Ember
              hand Caller
              enemy hp 50
              player energy 9
              play Caller
              expect enemy.hp == 41
              expect draw.count == 0
            """);

        /// <summary>A test's own <c>play</c> still puts a card in hand by name and plays it from there.</summary>
        [Fact]
        public void A_play_in_a_tests_own_body_is_still_the_tests_verb() => Passes("""
            card "Ember"
              cost 0
              target enemy
              tags attack
              effect:
                deal 9 to target

            test "the card is not in hand, and the test puts it there"
              enemy hp 50
              player energy 9
              play Ember on enemy
              expect enemy.hp == 41
              expect discard.count == 1
            """);

        // Targeting ----------------------------------------------------------------------------

        private const string Aimless = """
            enemy "Dummy"
              hp 50

            card "Aimless"
              cost 0
              target enemy
              effect:
                deal 5 to target

            card "Roll"
              cost 0
              effect:
                play draw.first

            """;

        /// <summary>
        /// The decisive one. <c>TryResolveTarget</c> consults the chooser when a <c>target enemy</c>
        /// card arrives with no target and more than one enemy is alive — which, under the real
        /// game's <see cref="DeferredChooser"/>, would raise a targeting dialog in the middle of "play
        /// the top card of your draw pile". An automatic play rolls instead, and never asks.
        /// </summary>
        [Fact]
        public void An_automatic_play_never_prompts_for_a_target()
        {
            var chooser = new DeferredChooser();
            CardRuntime runtime = Aimed(chooser, seed: 1);

            PlayResult result = runtime.Play("Roll");

            Assert.Equal(PlayResult.Played, result);
            Assert.Null(runtime.Pending);
            Assert.Equal(95, runtime.State.Actors(Team.Enemy).Sum(e => Hp(e)));
        }

        /// <summary>The roll comes from the game's own snapshotted RNG, so it replays and it saves.</summary>
        [Fact]
        public void The_rolled_target_is_reproducible_and_survives_a_snapshot()
        {
            int Aim(ulong seed)
            {
                CardRuntime runtime = Aimed(new FirstOptionChooser(), seed);
                Assert.Equal(PlayResult.Played, runtime.Play("Roll"));
                return runtime.State.Actors(Team.Enemy).First(e => Hp(e) == 45).Id;
            }

            Assert.Equal(Aim(7), Aim(7));

            // And the same again across a save taken before the play.
            CardRuntime saved = Aimed(new FirstOptionChooser(), seed: 7);
            GameSnapshot before = saved.Capture();
            Assert.Equal(PlayResult.Played, saved.Play("Roll"));
            int hit = saved.State.Actors(Team.Enemy).First(e => Hp(e) == 45).Id;

            saved.Restore(before);
            Assert.Equal(PlayResult.Played, saved.Play("Roll"));
            Assert.Equal(hit, saved.State.Actors(Team.Enemy).First(e => Hp(e) == 45).Id);
        }

        /// <summary>
        /// The roll goes through <c>LegalTargets</c>, which asks the <c>targetable</c> channel, so a
        /// Taunt binds an automatic play exactly as it binds a hand-played one.
        /// </summary>
        [Fact]
        public void A_taunt_binds_an_automatic_play_as_it_binds_a_hand_played_one() => Passes("""
            keyword "Taunt"

            enemy "Guard"
              hp 50
              tags taunt

            enemy "Squishy"
              hp 50

            card "Aimless"
              cost 0
              target enemy
              effect:
                deal 5 to target

            card "Roll"
              cost 0
              effect:
                play draw.first

            relic "Wall"
              modify targetable of enemies where not it.has(tag:taunt): set 0

            test "only the taunting enemy can be rolled"
              relic Wall
              enemy Guard hp 50
              enemy Squishy hp 50
              deck Aimless
              hand Roll
              player energy 9
              play Roll
              expect enemy1.hp == 45
              expect enemy2.hp == 50
            """);

        /// <summary>
        /// A card belongs to whoever controls it, so an enemy playing one out of its own pile pays
        /// out of its own resource and aims at the player. That falls out of <c>PlayCore</c> unchanged.
        /// </summary>
        [Fact]
        public void An_enemys_card_is_played_by_the_enemy_and_paid_for_by_the_enemy()
        {
            CardRuntime runtime = Create("""
                card "Slash"
                  cost 2
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
            duellist.SetBase("energy", 5);
            Start(runtime);

            runtime.AddCard("Slash", Zones.Draw, owner: duellist);
            runtime.Execute("play draw.first", duellist);

            Assert.Equal(73, Hp(runtime.Player!));
            Assert.Equal(40, Hp(duellist));
            Assert.Equal(3, duellist.GetInt("energy"));
            Assert.Equal(3, runtime.Player!.GetInt("energy"));
            Assert.Single(runtime.State.ZoneOf(duellist, Zones.Discard));
        }

        /// <summary>
        /// A choice inside a played card's effect uses the machinery unchanged: only the outermost
        /// call arms the rollback, so the whole outer action goes back and replays once the answer
        /// arrives — the nested play included.
        /// </summary>
        [Fact]
        public void A_choice_inside_a_nested_play_rolls_the_whole_outer_action_back_and_replays_it()
        {
            ContentLibrary content = Load("""
                enemy "Dummy"
                  hp 100

                card "Filler"
                  cost 0
                  effect:
                    block 0

                card "Sift"
                  cost 0
                  effect:
                    choose 1 from hand as picked
                    exhaust picked

                card "Outer"
                  cost 0
                  effect:
                    play draw.first
                    block 3
                """);

            var chooser = new DeferredChooser();
            var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 1, Chooser = chooser });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Sift", Zones.Draw);
            Entity outer = runtime.AddCard("Outer", Zones.Hand);
            runtime.AddCard("Filler", Zones.Hand);
            runtime.AddCard("Filler", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            ulong before = runtime.State.ComputeHash();
            Assert.Equal(PlayResult.ChoicePending, runtime.Play(outer, null));

            // Nothing of the outer action survived the rollback, the nested play included.
            Assert.Equal(before, runtime.State.ComputeHash());
            Assert.NotNull(runtime.Pending);
            Assert.Single(runtime.State.ZoneOf(runtime.Player, Zones.Draw));

            Entity picked = runtime.Pending!.Options.First(o => o.Name == "Filler");
            Assert.Equal(PlayResult.Played, runtime.Answer(picked.Id));

            Assert.Single(runtime.State.ZoneOf(runtime.Player, Zones.Exhaust));
            Assert.Equal(3, runtime.Player!.GetInt("block"));
            Assert.Empty(runtime.State.ZoneOf(runtime.Player, Zones.Draw));
        }

        // Hosts ----------------------------------------------------------------------------------

        /// <summary>
        /// A host that plays a card from inside <see cref="IEffectHost.OnEvent"/> now has its drain
        /// and its battle-over check done by the outer action rather than by the inner play. The
        /// triggers still resolve, in order, and the battle still ends.
        /// </summary>
        [Fact]
        public void A_host_that_plays_a_card_from_inside_an_event_still_resolves_and_still_ends_the_battle()
        {
            ContentLibrary content = Load("""
                enemy "Paper"
                  hp 4

                card "Finisher"
                  cost 0
                  target enemy
                  tags attack
                  effect:
                    deal 9 to target

                card "Signal"
                  cost 0
                  effect:
                    block 1
                """);

            var host = new PlayingHost();
            var runtime = new CardRuntime(content, new RuntimeOptions { Seed = 1, Host = host });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Paper");
            runtime.AddCard("Finisher", Zones.Hand);
            Entity signal = runtime.AddCard("Signal", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            host.Runtime = runtime;
            Assert.Equal(PlayResult.Played, runtime.Play(signal, null));

            Assert.True(runtime.Won);
            Assert.False(runtime.State.InBattle);
            Assert.Equal(0, runtime.Interpreter.PendingTriggers);
        }

        private sealed class PlayingHost : EffectHostBase
        {
            private bool _done;

            public CardRuntime? Runtime { get; set; }

            public override void OnEvent(GameEvent gameEvent)
            {
                if (_done || Runtime == null || gameEvent.Name != "gained_block") return;
                _done = true;

                Entity? finisher = Runtime.State.ZoneOf(Runtime.Player, Zones.Hand)
                    .FirstOrDefault(c => c.Name == "Finisher");
                if (finisher != null) Runtime.Play(finisher, Runtime.State.Actors(Team.Enemy).FirstOrDefault());
            }
        }

        // Refusals -------------------------------------------------------------------------------

        [Fact]
        public void A_definition_is_refused_and_names_create()
        {
            string failure = FirstFailure("""
                card "Ember"
                  cost 0
                  target enemy
                  effect:
                    deal 9 to target

                card "Cheat"
                  cost 0
                  effect:
                    play Ember

                test "content is not a card in a pile"
                  enemy hp 50
                  hand Cheat
                  player energy 9
                  play Cheat
                """);

            Assert.Contains("`play` plays a card that is in a pile", failure);
            Assert.Contains("create Ember into hand", failure);
            Assert.Contains("play created.first", failure);
        }

        /// <summary>
        /// A quoted name is text, not a definition, and text is not a card in a pile either. Without
        /// this it would evaluate to no entities at all and play nothing, silently.
        /// </summary>
        [Fact]
        public void A_quoted_name_is_refused_the_same_way_and_quoted_back()
        {
            string failure = FirstFailure("""
                card "Fire Bolt"
                  cost 0
                  target enemy
                  effect:
                    deal 4 to target

                card "Cheat"
                  cost 0
                  effect:
                    play "Fire Bolt"

                test "a quoted name is content too"
                  enemy hp 50
                  hand Cheat
                  player energy 9
                  play Cheat
                """);

            Assert.Contains("`play` plays a card that is in a pile", failure);
            Assert.Contains("create \"Fire Bolt\" into hand", failure);
        }

        /// <summary>
        /// Mayhem: "at the start of your turn, play the top card of your draw pile". The effect's own
        /// target is inherited only where the card could really be pointed at it, and in a listener it
        /// usually cannot be — <c>turn_start</c> is about the actor whose turn started — so handing
        /// that to a <c>target enemy</c> card would refuse the play and nothing would say so.
        /// </summary>
        [Fact]
        public void A_play_in_a_listener_rolls_a_target_instead_of_inheriting_the_events() => Passes("""
            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            status "Mayhem"
              flags persistent
              on turn_start(target:owner):
                play draw.first, free

            card "Bring Mayhem"
              cost 0
              tags power
              effect:
                apply Mayhem to player

            relic "Sweeper"
              on turn_end(target:owner):
                play draw.first, free

            test "a power that plays the top card at the start of the turn"
              hand "Bring Mayhem"
              deck 8 Strike
              enemy hp 60
              player energy 9
              play "Bring Mayhem"
              end turn
              expect enemy.hp == 54
              expect draw.count == 2

            test "a relic that plays the top card at the end of the turn"
              relic Sweeper
              deck 8 Strike
              enemy hp 60
              player energy 9
              end turn
              expect enemy.hp == 54
              expect draw.count == 2
            """);

        /// <summary>
        /// A target written with <c>on</c> is an instruction rather than a hint, so it is used as it
        /// stands and an illegal one refuses the play instead of being quietly swapped for a roll.
        /// </summary>
        [Fact]
        public void A_target_written_with_on_is_used_as_it_stands() => Passes("""
            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Aim"
              cost 0
              effect:
                play draw.first on player, free
                if played == none:
                  deal 3 to player

            test "aimed at something a `target enemy` card cannot take"
              deck Strike
              hand Aim
              enemy hp 60
              player hp 40
              player energy 9
              play Aim
              expect enemy.hp == 60
              expect player.hp == 37
              expect draw.count == 1
            """);

        // Helpers ----------------------------------------------------------------------------

        private static CardRuntime Aimed(IChoiceProvider chooser, ulong seed)
        {
            ContentLibrary content = Load(Aimless);
            var runtime = new CardRuntime(content, new RuntimeOptions { Seed = seed, Chooser = chooser });
            runtime.CreatePlayer();
            runtime.SpawnEnemy("Dummy");
            runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Aimless", Zones.Draw);
            runtime.AddCard("Roll", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

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
            ContentLibrary content = ContentLibrary.FromText(dsl, "play-verb.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());
            return content;
        }
    }
}
