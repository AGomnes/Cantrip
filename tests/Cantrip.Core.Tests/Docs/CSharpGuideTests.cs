using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Linting;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Docs
{
    /// <summary>
    /// What docs/csharp.md tells a game about presenting a battle: the order events arrive in, the
    /// changes that raise none, what a pending choice looks like, the members a battle screen reads,
    /// and how a restored game is found again. A failure here means the guide describes something the
    /// runtime no longer does.
    /// </summary>
    public sealed class CSharpGuideTests
    {
        private const string Content = """
            enemy "Jaw Worm"
              hp 40
              move "Chomp":
                deal 11 to player
              move "Bellow":
                gain 3 Strength
                block 6
              pattern cycle Chomp, Bellow

            status "Strength"
              stacking intensity
              modify damage: +stacks

            status "Poison"
              stacking intensity
              on turn_end:
                deal stacks to owner, ignore block
                stacks -1

            status "Weak"
              stacking duration
              modify damage: x0.75

            relic "Thorn Ring"
              on owner.damaged(source:enemies):
                deal 3 to event.source

            relic "Block Ledger"
              on block_changed:
                log "block" event.new

            enemy "Snapper"
              hp 40
              on turn_start:
                block 4
              move "Snap":
                deal 5 to player
                next turn: gain 2 Strength

            enemy "Sleeper"
              hp 40
              move "Doze":
                block 1

            enemy "Slime King"
              hp 60
              phase Broken when hp <= max_hp / 2
              move "Chomp":
                deal 11 to player
              move "Split" phase Broken:
                deal 5 to player
              pattern cycle Split, Chomp

            enemy "Slime Queen"
              hp 60
              phase Broken when hp <= max_hp / 2, retelegraph
              move "Chomp":
                deal 11 to player
              move "Split" phase Broken:
                deal 5 to player
              pattern cycle Split, Chomp

            card "Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 6 to target

            card "Twin Strike"
              cost 1
              target enemy
              tags attack
              effect:
                deal 3 to target
                deal 3 to target
                apply Poison 2 to target

            card "Tempo"
              cost 1
              target enemy
              tags attack
              rarity rare
              art "cards/tempo.png"
              effect:
                deal 1 to target

            card "Defend"
              cost 1
              effect:
                block 5

            card "Ghost"
              cost 1
              tags ethereal
              effect:
                block 1

            card "Survey"
              cost 1
              effect:
                discard 1
                draw 1

            card "Burn Out"
              cost 0
              effect:
                exhaust 1

            card "Pick"
              cost 0
              effect:
                choose 1 from hand as picked
                exhaust picked

            card "Seek"
              cost 0
              effect:
                discover 3 cards where tag:attack as found
                create found

            card "Flex"
              cost 0
              effect:
                gain 2 Strength

            card "Brand"
              cost 0
              target enemy
              effect:
                add tag:branded to target
            """;

        /// <summary>One reported event, copied when it arrived, with where its card was at that moment.</summary>
        private sealed record Seen(GameEvent Event, string Name, Entity? Source, Entity? Target, string? CardZone);

        private sealed class Recorder : EffectHostBase
        {
            public List<Seen> Events { get; } = new List<Seen>();

            public override void OnEvent(GameEvent gameEvent) =>
                Events.Add(new Seen(gameEvent, gameEvent.Name, gameEvent.Source, gameEvent.Target, gameEvent.Card?.Zone));

            public List<string> Names() => Events.Select(e => e.Name).ToList();
        }

        private static ContentLibrary Library()
        {
            ContentLibrary library = ContentLibrary.FromText(Content);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());
            return library;
        }

        private static CardRuntime Start(out Recorder host, out Entity player, IChoiceProvider? chooser = null, int enemies = 1, bool thornRing = false, string[]? hand = null, string enemy = "Jaw Worm")
        {
            host = new Recorder();
            var runtime = new CardRuntime(Library(), new RuntimeOptions { Seed = 7, Host = host, Chooser = chooser });
            player = runtime.CreatePlayer(hp: 80, maxEnergy: 3);
            if (thornRing) runtime.AddRelic("Thorn Ring");
            for (int i = 0; i < enemies; i++) runtime.SpawnEnemy(enemy);

            // Enough in the draw pile that no turn in these tests has to reshuffle.
            for (int i = 0; i < 20; i++) runtime.AddCard("Defend");
            foreach (string card in hand ?? Array.Empty<string>()) runtime.AddCard(card, Zones.Hand);

            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            host.Events.Clear();
            return runtime;
        }

        // Presenting events in a frame loop ------------------------------------------------------

        [Fact]
        public void A_played_card_reports_its_hits_before_card_played_and_reaches_the_discard_pile_silently()
        {
            CardRuntime runtime = Start(out Recorder host, out Entity player, hand: new[] { "Twin Strike" });
            Entity card = runtime.State.ZoneOf(player, Zones.Hand).Single();
            Entity worm = runtime.State.Actors(Team.Enemy).Single();

            Assert.Equal(PlayResult.Played, runtime.Play(card, worm));

            Assert.Equal(new[] { "damaged", "damaged", "status_applied", "card_played" }, host.Names());

            // While its own events resolve, the card is in the play zone.
            Seen played = host.Events.Last();
            Assert.Same(card, played.Event.Card);
            Assert.Equal(Zones.Play, played.CardZone);
            Assert.Equal(1, played.Event.Amount.ToInt());   // the energy paid, which raises nothing of its own

            // It then reaches the discard pile with no event, and the energy went without one too.
            Assert.Equal(Zones.Discard, card.Zone);
            Assert.DoesNotContain(host.Events, e => e.Name is "discarded" or "moved" or "energy_changed");
            Assert.Equal(2, player.GetInt("energy"));
        }

        [Fact]
        public void An_enemy_move_reports_its_hits_before_the_move_and_the_reactions_after_it()
        {
            CardRuntime runtime = Start(out Recorder host, out Entity player, enemies: 2, thornRing: true);
            Entity first = runtime.State.Actors(Team.Enemy)[0];
            Entity second = runtime.State.Actors(Team.Enemy)[1];
            Entity ring = runtime.State.ZoneOf(player, Zones.Relics).Single();

            runtime.EndTurn();

            var enemyTurn = host.Events
                .SkipWhile(e => !(e.Name == "turn_start" && e.Source == first))
                .TakeWhile(e => !(e.Name == "turn_start" && e.Source == player))
                .Select(e => (e.Name, e.Source, e.Target))
                .ToList();

            Assert.Equal(new (string, Entity?, Entity?)[]
            {
                ("turn_start", first, first),
                ("turn_start", second, second),
                ("damaged", first, player),
                ("move", first, player),
                ("damaged", ring, first),
                ("damaged", second, player),
                ("move", second, player),
                ("damaged", ring, second),
                ("turn_end", first, first),
                ("turn_end", second, second),
            }, enemyTurn);
        }

        [Fact]
        public void The_guides_battle_screen_shows_each_move_before_its_hits_and_the_reactions_after()
        {
            CardRuntime runtime = Start(out Recorder host, out Entity player, enemies: 2, thornRing: true);
            Entity first = runtime.State.Actors(Team.Enemy)[0];
            Entity second = runtime.State.Actors(Team.Enemy)[1];
            Entity ring = runtime.State.ZoneOf(player, Zones.Relics).Single();

            runtime.EndTurn();

            List<GameEvent> shown = ShownInOrder(host);

            var enemyTurn = shown
                .SkipWhile(e => !(e.Name == "turn_start" && e.Source == first))
                .TakeWhile(e => !(e.Name == "turn_start" && e.Source == player))
                .Select(e => (e.Name, e.Source, e.Target))
                .ToList();

            Assert.Equal(new (string, Entity?, Entity?)[]
            {
                ("turn_start", first, first),
                ("turn_start", second, second),
                ("move", first, player),
                ("damaged", first, player),
                ("damaged", ring, first),
                ("move", second, player),
                ("damaged", second, player),
                ("damaged", ring, second),
                ("turn_end", first, first),
                ("turn_end", second, second),
            }, enemyTurn);
            Assert.Equal("Chomp", shown.First(e => e.Name == "move").Data["move"].Text);
        }

        /// <summary>The lookahead from the guide's BattleScreen.TakeNext, run over the real queue.</summary>
        private static List<GameEvent> ShownInOrder(Recorder host)
        {
            List<GameEvent> queue = host.Events.Select(e => e.Event).ToList();
            var shown = new List<GameEvent>();
            while (queue.Count > 0)
            {
                GameEvent next = queue[0];
                int index = 0;
                if (next.Name != "turn_start" && next.Name != "turn_end")
                    index = Math.Max(0, queue.FindIndex(e => e.Name == "move" && e.Source == next.Source));

                shown.Add(queue[index]);
                queue.RemoveAt(index);
            }
            return shown;
        }

        [Fact]
        public void An_enemys_own_listeners_and_scheduled_work_name_it_as_source_but_a_status_names_itself()
        {
            CardRuntime runtime = Start(out Recorder host, out Entity player, enemy: "Snapper");
            Entity snapper = runtime.State.Actors(Team.Enemy).Single();
            runtime.ApplyStatus("Poison", snapper, 3);
            host.Events.Clear();

            runtime.EndTurn();

            // `on turn_start: block 4` on the enemy itself names the enemy, and comes before its move.
            List<GameEvent> queue = host.Events.Select(e => e.Event).ToList();
            int block = queue.FindIndex(e => e.Name == "gained_block" && e.Target == snapper);
            int move = queue.FindIndex(e => e.Name == "move");
            Assert.Same(snapper, queue[block].Source);
            Assert.True(block < move);

            // So the guide's screen shows the move before that block, as the guide warns.
            List<GameEvent> shown = ShownInOrder(host);
            Assert.True(shown.FindIndex(e => e.Name == "move") < shown.FindIndex(e => e.Name == "gained_block" && e.Target == snapper));

            // Poison's tick names the status, so the screen leaves it where it came.
            GameEvent tick = queue.Single(e => e.Name == "damaged" && e.Target == snapper);
            Assert.Equal("Poison", tick.Source!.Name);
            Assert.Equal(EntityKind.Status, tick.Source.Kind);
            Assert.True(shown.IndexOf(tick) > shown.FindIndex(e => e.Name == "move"));

            // Work the move scheduled with `next turn:` names the enemy when it runs.
            host.Events.Clear();
            runtime.EndTurn();
            GameEvent strength = host.Events.Select(e => e.Event).Single(e => e.Name == "status_applied" && e.Target == snapper);
            Assert.Same(snapper, strength.Source);
        }

        [Fact]
        public void The_changes_the_guide_lists_raise_no_event()
        {
            var host = new Recorder();
            var runtime = new CardRuntime(Library(), new RuntimeOptions { Seed = 7, Host = host });
            Entity player = runtime.CreatePlayer(hp: 80, maxEnergy: 3);
            Entity worm = runtime.SpawnEnemy("Jaw Worm");
            for (int i = 0; i < 20; i++) runtime.AddCard("Defend");
            Entity defend = runtime.AddCard("Defend", Zones.Hand);
            Entity kept = runtime.AddCard("Strike", Zones.Hand);
            Entity ghost = runtime.AddCard("Ghost", Zones.Hand);
            runtime.AddCard("Flex", Zones.Hand);
            runtime.AddCard("Flex", Zones.Hand);
            runtime.AddCard("Brand", Zones.Hand);

            // The draw pile is shuffled with no `shuffled`.
            runtime.StartBattle(shuffle: true, drawOpeningHand: false);
            Assert.DoesNotContain(host.Events, e => e.Name == "shuffled");
            string? intent = worm.Intent;

            // A second `gain` on a status the player already has raises nothing; the first applied it.
            Assert.Equal(PlayResult.Played, runtime.Play("Flex"));
            Assert.Contains(host.Events, e => e.Name == "status_applied");
            host.Events.Clear();
            Assert.Equal(PlayResult.Played, runtime.Play("Flex"));
            Assert.Equal(4, player.CounterOf("Strength"));
            Assert.Equal(new[] { "card_played" }, host.Names());

            // Tags come and go without one.
            host.Events.Clear();
            Assert.Equal(PlayResult.Played, runtime.Play("Brand", worm));
            Assert.True(worm.HasTag("branded"));
            Assert.Equal(new[] { "card_played" }, host.Names());

            Assert.Equal(PlayResult.Played, runtime.Play(defend));
            Assert.True(player.GetInt("block") > 0);
            runtime.ApplyStatus("Weak", player, 2);
            host.Events.Clear();

            runtime.EndTurn();

            // The hand is discarded without an event; the ethereal card is exhausted with one.
            Assert.Equal(Zones.Discard, kept.Zone);
            Assert.Equal(Zones.Exhaust, ghost.Zone);
            Assert.Contains(host.Events, e => e.Name == "exhausted" && e.Target == ghost);
            Assert.DoesNotContain(host.Events, e => e.Name is "discarded" or "moved");

            // Block falls to 0 and energy refills, Weak counts down, and a new intent is rolled, all silently.
            Assert.Equal(0, player.GetInt("block"));
            Assert.Equal(3, player.GetInt("energy"));
            Assert.Equal(1, player.CounterOf("Weak"));
            Assert.NotEqual(intent, worm.Intent);
            Assert.DoesNotContain(host.Events, e => e.Name.EndsWith("_changed", StringComparison.Ordinal));

            // When the battle ends, the player's statuses go and every card returns to the draw pile, silently.
            host.Events.Clear();
            runtime.Execute("kill enemies");
            Assert.Equal(true, runtime.Won);
            Assert.Equal("battle_end", host.Events.Last().Name);
            Assert.Empty(player.Attached);
            Assert.DoesNotContain(host.Events, e => e.Name == "status_removed");
            Assert.Equal(Zones.Draw, kept.Zone);
            Assert.Equal(Zones.Draw, ghost.Zone);
        }

        [Theory]
        [InlineData("Slime King", "Chomp")]    // the intent on show stays
        [InlineData("Slime Queen", "Split")]   // retelegraph rolls it again
        public void A_hit_across_a_phase_threshold_changes_the_phase_without_an_event(string enemy, string intent)
        {
            CardRuntime runtime = Start(out Recorder host, out _, enemy: enemy);
            Entity slime = runtime.State.Actors(Team.Enemy).Single();
            Assert.Null(slime.Phase);
            Assert.Equal("Chomp", slime.Intent);

            runtime.Execute("deal 35 to target", target: slime);

            Assert.Equal("Broken", slime.Phase);
            Assert.Equal(intent, slime.Intent);
            Assert.Equal(new[] { "damaged" }, host.Names());
        }

        [Fact]
        public void Stat_changes_raise_stat_changed_only_when_content_listens_resets_included()
        {
            // Without a listener: losing hp and the turn-start block reset raise nothing.
            CardRuntime quiet = Start(out Recorder quietHost, out Entity quietPlayer, enemy: "Sleeper", hand: new[] { "Defend" });
            quiet.Execute("lose 3 hp");
            Assert.Equal(77, quietPlayer.GetInt("hp"));
            Assert.Empty(quietHost.Events);

            Assert.Equal(PlayResult.Played, quiet.Play("Defend"));
            quietHost.Events.Clear();
            quiet.EndTurn();
            Assert.Equal(0, quietPlayer.GetInt("block"));
            Assert.DoesNotContain(quietHost.Events, e => e.Name == "block_changed");

            // With one, the reset raises block_changed, marked as a reset.
            CardRuntime heard = Start(out Recorder heardHost, out Entity heardPlayer, enemy: "Sleeper", hand: new[] { "Defend" });
            heard.AddRelic("Block Ledger");
            Assert.Equal(PlayResult.Played, heard.Play("Defend"));
            heardHost.Events.Clear();
            heard.EndTurn();
            GameEvent reset = heardHost.Events.Select(e => e.Event).Single(e => e.Name == "block_changed" && e.Target == heardPlayer);
            Assert.True(reset.Data["reset"].AsBool());
        }

        [Fact]
        public void Of_the_setup_calls_only_AddRelic_and_ApplyStatus_raise_events()
        {
            var host = new Recorder();
            var runtime = new CardRuntime(Library(), new RuntimeOptions { Seed = 7, Host = host });
            Entity player = runtime.CreatePlayer(hp: 80, maxEnergy: 3);
            runtime.AddCard("Strike");
            runtime.AddCard("Defend", Zones.Hand);
            runtime.AddDeck("Strike", "Defend");
            Entity worm = runtime.SpawnEnemy("Jaw Worm");
            Assert.Empty(host.Events);

            runtime.AddRelic("Thorn Ring");
            Assert.Equal(new[] { "obtained" }, host.Names());

            host.Events.Clear();
            runtime.ApplyStatus("Weak", worm, 2);
            Assert.Equal(new[] { "status_applied" }, host.Names());
            Assert.Same(player, host.Events.Single().Source);
        }

        // Player choices ---------------------------------------------------------------------------

        [Fact]
        public void A_pending_choice_leaves_the_card_in_hand_unpaid_and_out_of_its_options()
        {
            CardRuntime runtime = Start(out Recorder host, out Entity player, new DeferredChooser(), hand: new[] { "Survey", "Strike", "Defend" });
            Entity survey = runtime.State.ZoneOf(player, Zones.Hand).First(c => c.Name == "Survey");

            Assert.Equal(PlayResult.ChoicePending, runtime.Play(survey));

            PendingChoice choice = runtime.Pending!;
            Assert.Equal("discard 1", choice.Prompt);
            Assert.Same(player, choice.Chooser);
            Assert.Equal(Zones.Hand, survey.Zone);
            Assert.Equal(3, player.GetInt("energy"));
            Assert.DoesNotContain(survey, choice.Options);
            Assert.All(choice.Options, option => Assert.Equal(Zones.Hand, option.Zone));
            Assert.Empty(host.Events);

            // Answered with the entities themselves, in a list, as the guide's second example does.
            Assert.Equal(PlayResult.Played, runtime.Answer(new List<Entity> { choice.Options[0] }));
            Assert.Equal(Zones.Discard, survey.Zone);
            Assert.Equal(2, player.GetInt("energy"));
        }

        [Theory]
        [InlineData("Burn Out", "exhaust 1")]
        [InlineData("Pick", "choose 1")]
        [InlineData("Seek", "discover 1 of 3")]
        [InlineData("Tempo", "choose a target")]
        public void A_pending_choice_prompt_is_the_engines_short_summary(string card, string prompt)
        {
            CardRuntime runtime = Start(out _, out Entity player, new DeferredChooser(), enemies: 2, hand: new[] { card, "Strike", "Defend" });
            Entity played = runtime.State.ZoneOf(player, Zones.Hand).First(c => c.Name == card);

            Assert.Equal(PlayResult.ChoicePending, runtime.Play(played));
            Assert.Equal(prompt, runtime.Pending!.Prompt);
        }

        // What a battle screen reads ---------------------------------------------------------------

        [Fact]
        public void Presentation_properties_stay_on_the_definition_without_a_warning()
        {
            ContentLibrary content = Library();
            EntityDefinition tempo = content.Find("Tempo", "card")!;

            Assert.Equal("rare", tempo.Word("rarity"));
            Assert.Equal("cards/tempo.png", tempo.ReadString("art"));
            Assert.Contains(tempo, content.Pool("card").Where(card => card.HasTag("attack")));

            IReadOnlyList<Diagnostic> problems = Linter.Lint(content);
            Assert.DoesNotContain(problems, d => d.Severity != DiagnosticSeverity.Info && d.Message.Contains("Tempo", StringComparison.Ordinal));
        }

        [Fact]
        public void A_dead_enemy_leaves_Actors_and_waits_in_the_dead_zone()
        {
            CardRuntime runtime = Start(out _, out _, enemies: 2);
            Entity first = runtime.State.Actors(Team.Enemy)[0];

            runtime.Execute("kill target", target: first);

            Assert.True(first.IsDead);
            Assert.Equal(Zones.Dead, first.Zone);
            Assert.DoesNotContain(first, runtime.State.Actors(Team.Enemy));
        }

        // Save and load ----------------------------------------------------------------------------

        [Fact]
        public void A_game_restored_into_a_new_runtime_is_found_again_through_Player_Actors_and_Find()
        {
            CardRuntime runtime = Start(out _, out Entity player, enemies: 2, hand: new[] { "Strike" });
            Entity worm = runtime.State.Actors(Team.Enemy)[1];
            Entity strike = runtime.State.ZoneOf(player, Zones.Hand).Single();
            GameSnapshot save = runtime.Capture();

            var fresh = new CardRuntime(runtime.Content, new RuntimeOptions { Seed = 7 });
            fresh.Restore(save);

            Assert.NotSame(player, fresh.Player);
            Assert.Equal(player.Id, fresh.Player!.Id);
            Assert.Equal(new[] { "Jaw Worm", "Jaw Worm" }, fresh.State.Actors(Team.Enemy).Select(e => e.Name));

            Entity? again = fresh.State.Find(worm.Id);
            Assert.NotNull(again);
            Assert.NotSame(worm, again);
            Assert.Same(fresh.State.Actors(Team.Enemy)[1], again);
            Assert.Equal("Strike", fresh.State.Find(strike.Id)!.Name);
        }
    }
}
