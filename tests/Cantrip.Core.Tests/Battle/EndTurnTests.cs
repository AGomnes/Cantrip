#nullable enable
using System.Collections.Generic;
using System.Linq;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Battle
{
    /// <summary>
    /// The full EndTurn cycle: player turn end, the enemy turn with its moves, and the next player
    /// turn. Covers resource resets, status decay, enemy patterns and when intents are rolled.
    /// </summary>
    public sealed class EndTurnTests
    {
        private const string Content = """
            card "Defend"
              cost 1
              effect:
                block 5

            card "Fuse"
              cost 0
              effect:
                in 2 turns:
                  deal 7 to enemies

            status "Vulnerable"
              tags debuff
              stacking duration
              modify damage_taken: x1.5

            status "Poison"
              tags dot, poison, debuff
              stacking intensity
              on turn_end:
                deal stacks to owner, ignore block
                stacks -1

            status "Plating"
              stacking intensity
              decay 2 on turn_start

            resource "rage"
              min 0
              reset_to 0
              reset_on turn_end

            resource "mana"
              min 0
              max 10
              reset_to 4
              reset_on turn_start

            enemy "Brute"
              hp 50
              move "Smash":
                deal 8 to player
              move "Guard":
                block 6
              pattern cycle Smash, Guard

            enemy "Trio"
              hp 50
              move "A":
                block 1
              move "B":
                block 1
              move "C":
                block 1

            enemy "Picky"
              hp 50
              move "A":
                block 1
              move "B":
                block 1
              move "C":
                block 1
              pattern random_no_repeat

            enemy "Loner"
              hp 50
              move "Only":
                block 1
              pattern random_no_repeat

            enemy "Weighted"
              hp 50
              move "Common" weight 9:
                block 1
              move "Rare" weight 1:
                block 1
              move "Never" weight 0:
                block 1
              pattern random
            """;

        private static CardRuntime Setup(out Entity player, BattleEventRecorder? recorder = null, ulong seed = 1)
        {
            CardRuntime runtime = BattleKit.Create(Content, seed, host: recorder);
            player = runtime.CreatePlayer();
            return runtime;
        }

        private static List<string?> Moves(BattleEventRecorder recorder) =>
            recorder.Named("move").Select(e => e.DataText("move")).ToList();

        // Turn structure ------------------------------------------------------------------------

        [Fact]
        public void EndTurn_runs_player_turn_end_then_the_enemy_turn_then_the_next_player_turn()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out Entity player, recorder);
            Entity brute = runtime.SpawnEnemy("Brute");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            recorder.Clear();

            runtime.EndTurn();

            var lifecycle = recorder.Events
                .Where(e => e.Name == "turn_start" || e.Name == "turn_end" || e.Name == "move")
                // A move's source is the enemy and its target the player, so check for the brute first.
                .Select(e => (e.Name, e.Source == brute || e.Target == brute ? "brute" : e.Target == player ? "player" : "?"))
                .ToList();
            Assert.Equal(
                new[] { ("turn_end", "player"), ("turn_start", "brute"), ("move", "brute"), ("turn_end", "brute"), ("turn_start", "player") },
                lifecycle);

            Assert.Equal(2, runtime.State.Turn);
            Assert.Equal(1, runtime.State.Clock.Now);
            Assert.Equal(Team.Player, runtime.State.ActiveTeam);
        }

        [Fact]
        public void Energy_and_player_block_reset_at_the_next_player_turn()
        {
            CardRuntime runtime = Setup(out Entity player);
            runtime.SpawnEnemy("Brute");
            runtime.AddCard("Defend", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Play("Defend");
            Assert.Equal(2, player.GetInt("energy"));
            Assert.Equal(5, player.GetInt("block"));

            runtime.EndTurn();

            // Smash hit for 8 while the block was still up; block and energy then reset.
            Assert.Equal(77, player.GetInt("hp"));
            Assert.Equal(0, player.GetInt("block"));
            Assert.Equal(3, player.GetInt("energy"));
        }

        [Fact]
        public void Enemy_block_lasts_through_the_player_turn_and_resets_at_the_enemy_turn_start()
        {
            CardRuntime runtime = Setup(out _);
            Entity brute = runtime.SpawnEnemy("Brute");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.EndTurn(); // Smash
            Assert.Equal(0, brute.GetInt("block"));

            runtime.EndTurn(); // Guard
            Assert.Equal(6, brute.GetInt("block"));

            runtime.EndTurn(); // block resets, then Smash
            Assert.Equal(0, brute.GetInt("block"));
        }

        [Fact]
        public void Declared_resources_reset_on_their_own_event_and_respect_bounds()
        {
            CardRuntime runtime = Setup(out Entity player);
            runtime.SpawnEnemy("Trio");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            player.SetBase("mana", 9);
            player.SetBase("rage", 5);

            runtime.Execute("gain 20 mana");
            Assert.Equal(10, player.GetInt("mana"));

            runtime.EndTurn();

            Assert.Equal(4, player.GetInt("mana"));
            Assert.Equal(0, player.GetInt("rage"));
        }

        [Fact]
        public void EndTurn_outside_a_battle_does_nothing()
        {
            CardRuntime runtime = Setup(out _);
            runtime.SpawnEnemy("Brute");

            runtime.EndTurn();

            Assert.Equal(0, runtime.State.Turn);
            Assert.False(runtime.State.InBattle);
        }

        [Fact]
        public void In_n_turns_runs_when_the_turn_clock_gets_there()
        {
            CardRuntime runtime = Setup(out _);
            Entity brute = runtime.SpawnEnemy("Trio");
            runtime.AddCard("Fuse", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            runtime.Play("Fuse");
            runtime.EndTurn();
            Assert.Equal(50, brute.GetInt("hp"));

            // Fuse goes off at the start of turn 3. Trio's block from its last move is still up
            // during the player's turn, so it absorbs 1 of the 7.
            runtime.EndTurn();
            Assert.Equal(44, brute.GetInt("hp"));
            Assert.Equal(0, brute.GetInt("block"));
            Assert.Empty(runtime.State.Scheduled);
        }

        // Decay ---------------------------------------------------------------------------------

        [Fact]
        public void Duration_statuses_tick_down_at_their_hosts_turn_end_and_then_expire()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out Entity player, recorder);
            Entity brute = runtime.SpawnEnemy("Trio");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            Entity vulnerable = runtime.ApplyStatus("Vulnerable", brute, 2)!;
            Entity playerVulnerable = runtime.ApplyStatus("Vulnerable", player, 2)!;

            runtime.EndTurn();
            Assert.Equal(1, vulnerable.GetBase("duration").ToInt());
            Assert.Equal(1, playerVulnerable.GetBase("duration").ToInt());

            runtime.EndTurn();
            Assert.Null(brute.FindAttached("Vulnerable"));
            Assert.Null(player.FindAttached("Vulnerable"));
            Assert.True(vulnerable.IsRemoved);
            Assert.Equal(2, recorder.Named("status_removed").Count);
        }

        [Fact]
        public void Decay_can_be_tied_to_turn_start()
        {
            CardRuntime runtime = Setup(out Entity player);
            runtime.SpawnEnemy("Trio");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Plating", player, 5);

            runtime.EndTurn();
            Assert.Equal(3, player.StacksOf("Plating"));

            runtime.EndTurn();
            Assert.Equal(1, player.StacksOf("Plating"));

            runtime.EndTurn();
            Assert.Null(player.FindAttached("Plating"));
        }

        [Fact]
        public void Turn_end_listeners_on_a_status_only_hear_their_hosts_turn()
        {
            CardRuntime runtime = Setup(out Entity player);
            Entity trio = runtime.SpawnEnemy("Trio");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.ApplyStatus("Poison", player, 3);
            runtime.ApplyStatus("Poison", trio, 4);

            runtime.EndTurn();

            Assert.Equal(77, player.GetInt("hp"));
            Assert.Equal(2, player.StacksOf("Poison"));
            Assert.Equal(46, trio.GetInt("hp"));
            Assert.Equal(3, trio.StacksOf("Poison"));
        }

        // Enemy moves ---------------------------------------------------------------------------

        [Fact]
        public void A_cycle_pattern_uses_its_moves_in_order()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out _, recorder);
            runtime.SpawnEnemy("Brute");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            for (int i = 0; i < 5; i++) runtime.EndTurn();

            Assert.Equal(new[] { "Smash", "Guard", "Smash", "Guard", "Smash" }, Moves(recorder));
        }

        [Fact]
        public void Without_a_pattern_moves_cycle_in_declaration_order()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out _, recorder);
            runtime.SpawnEnemy("Trio");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            for (int i = 0; i < 6; i++) runtime.EndTurn();

            Assert.Equal(new[] { "A", "B", "C", "A", "B", "C" }, Moves(recorder));
        }

        [Fact]
        public void The_intent_shown_during_the_player_turn_is_the_move_the_enemy_uses()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out _, recorder, seed: 3);
            Entity picky = runtime.SpawnEnemy("Picky");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            for (int i = 0; i < 12; i++)
            {
                string? intent = picky.Intent;
                Assert.NotNull(intent);
                recorder.Clear();

                runtime.EndTurn();

                Assert.Equal(intent, Assert.Single(Moves(recorder)));
            }
        }

        [Fact]
        public void Intents_are_rolled_after_the_enemy_turn_not_before_it()
        {
            var recorder = new BattleEventRecorder();
            var intentWhileMoving = new List<string?>();
            recorder.Hook = e =>
            {
                if (e.Name == "move") intentWhileMoving.Add(e.Source!.Intent);
            };
            CardRuntime runtime = Setup(out _, recorder);
            Entity trio = runtime.SpawnEnemy("Trio");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            Assert.Equal("A", trio.Intent);

            runtime.EndTurn();
            Assert.Equal("B", trio.Intent);
            runtime.EndTurn();
            Assert.Equal("C", trio.Intent);

            Assert.Equal(new[] { "A", "B" }, intentWhileMoving);
        }

        [Fact]
        public void An_enemy_spawned_mid_battle_gets_an_intent_straight_away()
        {
            CardRuntime runtime = Setup(out _);
            runtime.SpawnEnemy("Trio");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            Entity late = runtime.SpawnEnemy("Brute");

            Assert.Equal("Smash", late.Intent);
        }

        [Fact]
        public void Random_no_repeat_never_uses_the_same_move_twice_in_a_row()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out _, recorder, seed: 42);
            runtime.SpawnEnemy("Picky");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            for (int i = 0; i < 150; i++) runtime.EndTurn();

            List<string?> moves = Moves(recorder);
            Assert.Equal(150, moves.Count);
            for (int i = 1; i < moves.Count; i++) Assert.NotEqual(moves[i - 1], moves[i]);
            Assert.Equal(new[] { "A", "B", "C" }, moves.Distinct().OrderBy(m => m, System.StringComparer.Ordinal));
        }

        [Fact]
        public void Random_no_repeat_with_a_single_move_repeats_it()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out _, recorder);
            runtime.SpawnEnemy("Loner");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);

            for (int i = 0; i < 3; i++) runtime.EndTurn();

            Assert.Equal(new[] { "Only", "Only", "Only" }, Moves(recorder));
        }

        [Fact]
        public void Random_patterns_follow_move_weights()
        {
            CardRuntime runtime = Setup(out _, seed: 9);
            Entity weighted = runtime.SpawnEnemy("Weighted");

            var counts = new Dictionary<string, int> { ["Common"] = 0, ["Rare"] = 0, ["Never"] = 0 };
            for (int i = 0; i < 2000; i++)
            {
                runtime.RollIntent(weighted);
                counts[weighted.Intent!]++;
            }

            Assert.Equal(0, counts["Never"]);
            Assert.InRange(counts["Common"], 1700, 1900);
            Assert.InRange(counts["Rare"], 100, 300);
        }

        [Fact]
        public void Random_intents_come_from_the_seeded_game_rng()
        {
            List<string> Rolls(ulong seed)
            {
                CardRuntime runtime = Setup(out _, seed: seed);
                Entity weighted = runtime.SpawnEnemy("Picky");
                var rolls = new List<string>();
                for (int i = 0; i < 30; i++)
                {
                    runtime.RollIntent(weighted);
                    rolls.Add(weighted.Intent!);
                }
                return rolls;
            }

            Assert.Equal(Rolls(5), Rolls(5));
            Assert.NotEqual(Rolls(5), Rolls(6));
        }

        [Fact]
        public void Dead_enemies_do_not_take_their_turn()
        {
            var recorder = new BattleEventRecorder();
            CardRuntime runtime = Setup(out Entity player, recorder);
            Entity first = runtime.SpawnEnemy("Brute");
            Entity second = runtime.SpawnEnemy("Brute");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            runtime.Execute("deal 100 to target", target: first);

            runtime.EndTurn();

            BattleRecordedEvent move = Assert.Single(recorder.Named("move"));
            Assert.Same(second, move.Source);
            Assert.Equal(72, player.GetInt("hp"));
        }
    }
}
