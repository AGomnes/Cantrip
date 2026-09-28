using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Syntax;
using Xunit;
using static Cantrip.Tests.Runtime.RuntimeTestKit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// The board as content sees it: the selectors that read it, the reach that narrows what may be
    /// pointed at, and the member write that moves somebody.
    /// </summary>
    public sealed class BoardLanguageTests
    {
        private const string Line = """
            board Line
              lanes 3
              ranks 3
              lane_word "floor"
              rank_word "slot"

            enemy "Goon"
              hp 30
              attack 4
              move "Swing":
                deal attack to player

            actor "Pawn"
              hp 10
              attack 2

            card "Lunge"
              cost 0
              range 1
              target enemy
              effect:
                deal 6 to target

            card "Longbow"
              cost 0
              range 2..3
              target enemy
              effect:
                deal 4 to target

            card "Haul"
              cost 0
              target enemy
              effect:
                target.rank = 0

            card "Climb"
              cost 0
              target ally
              effect:
                target.lane += 1
            """;

        private static CardRuntime Board()
        {
            CardRuntime runtime = Create(Line);
            Start(runtime);
            return runtime;
        }

        /// <summary>A host that keeps every event it is told about, so a test can read what was said.</summary>
        private sealed class Recorder : EffectHostBase
        {
            public List<GameEvent> Events { get; } = new List<GameEvent>();

            public override void OnEvent(GameEvent gameEvent) => Events.Add(gameEvent);

            public List<GameEvent> Named(string name) => Events.Where(e => e.Name == name).ToList();
        }

        /// <summary>An enemy standing on a named slot, for a fight with a shape to it.</summary>
        private static Entity Goon(CardRuntime runtime, int lane, int rank)
        {
            Entity goon = runtime.State.Instantiate(runtime.Content.Find("Goon", "enemy")!, null, Team.Enemy, Zones.Board);
            runtime.State.Assign(goon, lane, rank);
            return goon;
        }

        private static Entity Card(CardRuntime runtime, string name)
        {
            Entity card = runtime.State.Instantiate(runtime.Content.Find(name, "card")!, runtime.Player, Team.Player, Zones.Hand);
            return card;
        }

        // Selectors -------------------------------------------------------------------------

        /// <summary>
        /// `within` reaches across the board, which is the honest name for the question `adjacent`
        /// deliberately is not: making `adjacent` cross-side would turn an aura into a gift.
        /// </summary>
        [Fact]
        public void Within_reaches_both_sides_and_includes_who_it_measures_from()
        {
            CardRuntime runtime = Board();
            Entity front = Goon(runtime, 0, 0);
            Entity behind = Goon(runtime, 0, 1);
            Entity across = Goon(runtime, 2, 0);

            // The player holds floor 0, slot 0. Across the sides the rank term is a+b+1, so the
            // front Goon is 1 away, the one behind it 2, and the one two floors over 3.
            Assert.Equal(1, runtime.State.Distance(runtime.Player!, front));
            Assert.Equal(2, runtime.State.Distance(runtime.Player!, behind));
            Assert.Equal(3, runtime.State.Distance(runtime.Player!, across));

            IReadOnlyList<Entity> near = Eval(runtime, "within(player, 2)").AsEntities();
            Assert.Contains(runtime.Player!, near);
            Assert.Contains(front, near);
            Assert.Contains(behind, near);
            Assert.DoesNotContain(across, near);

            // `adjacent` stays on one side, so from the player it is the player's own side alone.
            Assert.DoesNotContain(front, Eval(runtime, "adjacent(player)").AsEntities());
        }

        /// <summary>
        /// Slots are the engine's and metres are the game's, so one word carries both and the unit
        /// is what tells them apart. A game with no host to ask gets told exactly that.
        /// </summary>
        [Fact]
        public void Within_with_a_unit_still_goes_to_the_host()
        {
            CardRuntime runtime = Board();
            Goon(runtime, 0, 0);

            RuntimeError error = Assert.Throws<RuntimeError>(() => Eval(runtime, "within(player, 5m)"));
            Assert.Contains("IEffectHost.TryCall", error.Message);
            Assert.Contains("counts slots", error.Message);
        }

        [Fact]
        public void Lane_and_rank_are_the_row_and_the_line_across_on_one_side()
        {
            CardRuntime runtime = Board();
            Entity front = Goon(runtime, 0, 0);
            Entity behind = Goon(runtime, 0, 1);
            Entity beside = Goon(runtime, 1, 0);

            IReadOnlyList<Entity> floor = Eval(runtime, "lane(first)", locals: new Dictionary<string, Entity> { ["first"] = front }).AsEntities();
            Assert.Equal(new[] { front, behind }, floor);

            IReadOnlyList<Entity> across = Eval(runtime, "rank(first)", locals: new Dictionary<string, Entity> { ["first"] = front }).AsEntities();
            Assert.Equal(new[] { front, beside }, across);

            // One side only: the player is at the same rank and is not in it.
            Assert.DoesNotContain(runtime.Player!, across);
        }

        [Fact]
        public void Distance_is_slots_and_says_so_even_for_something_off_the_board()
        {
            CardRuntime runtime = Board();
            Entity goon = Goon(runtime, 2, 2);

            Assert.Equal(2 + (0 + 2 + 1), EvalInt(runtime, "distance(player, it)", locals: new Dictionary<string, Entity> { ["it"] = goon }));

            Entity card = Card(runtime, "Lunge");
            Assert.Equal(int.MaxValue, EvalInt(runtime, "distance(player, it)", locals: new Dictionary<string, Entity> { ["it"] = card }));
        }

        /// <summary>
        /// A group of one answers where it stands like the one it holds. It used to fall through to
        /// the stat table, where nothing has a rank, so `created.rank` read 0 while
        /// `created.first.rank` read 2 — the same actor, two answers, and no message about it.
        /// </summary>
        [Fact]
        public void A_group_of_one_answers_its_lane_and_rank()
        {
            CardRuntime runtime = Board();
            Goon(runtime, 2, 2);

            Assert.Equal(1, EvalInt(runtime, "enemies.count"));
            Assert.Equal(2, EvalInt(runtime, "enemies.rank"));
            Assert.Equal(2, EvalInt(runtime, "enemies.lane"));
            Assert.Equal(2, EvalInt(runtime, "enemies.position"));
            Assert.Equal(EvalInt(runtime, "enemies.first.rank"), EvalInt(runtime, "enemies.rank"));
        }

        // Reach -----------------------------------------------------------------------------

        [Fact]
        public void Range_one_reaches_the_front_and_nothing_behind_it()
        {
            CardRuntime runtime = Board();
            Entity front = Goon(runtime, 0, 0);
            Entity behind = Goon(runtime, 0, 1);

            IReadOnlyList<Entity> legal = runtime.LegalTargets(Card(runtime, "Lunge"));
            Assert.Equal(new[] { front }, legal);
            Assert.DoesNotContain(behind, legal);
        }

        [Fact]
        public void A_span_cannot_shoot_point_blank()
        {
            CardRuntime runtime = Board();
            Entity front = Goon(runtime, 0, 0);
            Entity behind = Goon(runtime, 0, 1);

            IReadOnlyList<Entity> legal = runtime.LegalTargets(Card(runtime, "Longbow"));
            Assert.Equal(new[] { behind }, legal);
            Assert.DoesNotContain(front, legal);
        }

        /// <summary>
        /// The printed range is the base of the `range` channel, so a status shortens a longbow into
        /// a melee weapon rather than into something that can no longer reach anything at all.
        /// </summary>
        [Fact]
        public void A_status_on_the_channel_makes_everything_melee()
        {
            CardRuntime runtime = Create(Line + """

                status "Crippled"
                  stacking none
                  modify range: set 1
                """);
            Start(runtime);

            Entity front = Goon(runtime, 0, 0);
            Entity behind = Goon(runtime, 0, 1);
            Entity bow = Card(runtime, "Longbow");

            Assert.Equal(new[] { behind }, runtime.LegalTargets(bow));

            runtime.Interpreter.ApplyStatus(runtime.Content.Find("Crippled", "status")!, runtime.Player!, Num.One, null, new EvalContext(runtime.Player!));
            Assert.Equal(new[] { front }, runtime.LegalTargets(bow));
        }

        /// <summary>
        /// A card with no `range`, in a game with nothing on the channel, asks nothing: that is what
        /// every game did before reach existed, and it is why adding it moved no sample's numbers.
        /// </summary>
        [Fact]
        public void An_action_with_no_printed_range_reaches_everybody()
        {
            CardRuntime runtime = Board();
            Entity front = Goon(runtime, 0, 0);
            Entity far = Goon(runtime, 2, 2);

            Assert.Equal(new[] { front, far }, runtime.LegalTargets(Card(runtime, "Haul")).OrderBy(e => e.Lane).ToArray());
        }

        [Fact]
        public void An_enemy_move_honours_its_own_range()
        {
            CardRuntime runtime = Create("""
                board Line
                  lanes 1
                  ranks 3

                actor "Pawn"
                  hp 10

                enemy "Reaver"
                  hp 30
                  attack 4
                  move "Swing" range 1:
                    deal attack to target
                """);
            Start(runtime);

            Entity pawn = runtime.State.Instantiate(runtime.Content.Find("Pawn", "actor")!, null, Team.Player, Zones.Board);
            Entity reaver = runtime.State.Instantiate(runtime.Content.Find("Reaver", "enemy")!, null, Team.Enemy, Zones.Board);
            runtime.State.Assign(reaver, 0, 1);

            // The player is at slot 0 and the Pawn behind it at slot 1. The Reaver stands at slot 1
            // of its own side, so the player is 2 away and the Pawn 3: the swing reaches neither,
            // and a move whose every option is out of reach keeps the one it was given.
            Assert.Equal(2, runtime.State.Distance(reaver, runtime.Player!));
            Assert.Equal(3, runtime.State.Distance(reaver, pawn));

            runtime.EndTurn();
            Assert.Equal(80, Hp(runtime.Player!));

            // Standing at the front, the player is one step away and the swing lands.
            runtime.State.Assign(reaver, 0, 0);
            runtime.EndTurn();
            Assert.Equal(76, Hp(runtime.Player!));
        }

        // Movement --------------------------------------------------------------------------

        [Fact]
        public void Assigning_a_rank_moves_the_actor_and_swaps_whoever_is_there()
        {
            CardRuntime runtime = Board();
            Entity front = Goon(runtime, 0, 0);
            Entity behind = Goon(runtime, 0, 1);

            runtime.Play(Card(runtime, "Haul"), behind);

            Assert.Equal(0, behind.Rank);
            Assert.Equal(1, front.Rank);
        }

        [Fact]
        public void A_step_that_would_leave_the_board_stops_at_its_edge()
        {
            CardRuntime runtime = Board();
            Entity pawn = runtime.State.Instantiate(runtime.Content.Find("Pawn", "actor")!, null, Team.Player, Zones.Board);
            runtime.State.Assign(pawn, 2, 1);

            var context = new EvalContext(pawn) { Source = pawn };
            Assert.False(runtime.Interpreter.MoveOnBoard(pawn, lane: true, AssignOperator.Add, Num.One, context));
            Assert.Equal(2, pawn.Lane);

            Assert.True(runtime.Interpreter.MoveOnBoard(pawn, lane: false, AssignOperator.Subtract, Num.FromInt(5), context));
            Assert.Equal(0, pawn.Rank);
        }

        /// <summary>
        /// `position` reads a rank and always will, but it names one axis of a place that has two,
        /// so writing it would have to guess which. It used to write a shadowed stat in silence.
        /// </summary>
        [Fact]
        public void Assigning_position_says_to_write_rank()
        {
            CardRuntime runtime = Create(Line + """

                card "Slide"
                  cost 0
                  target enemy
                  effect:
                    target.position = 0
                """);
            Start(runtime);
            Entity goon = Goon(runtime, 0, 1);

            RuntimeError error = Assert.Throws<RuntimeError>(() => runtime.Play(Card(runtime, "Slide"), goon));
            Assert.Contains("Write `rank`", error.Message);
            Assert.Equal(1, goon.Rank);
        }

        [Fact]
        public void A_move_raises_moved_with_where_it_came_from_and_went()
        {
            var recorder = new Recorder();
            CardRuntime runtime = Create(Line, new RuntimeOptions { Host = recorder });
            Start(runtime);
            Entity goon = Goon(runtime, 1, 2);

            var context = new EvalContext(runtime.Player!) { Source = runtime.Player! };
            runtime.Interpreter.MoveOnBoard(goon, lane: false, AssignOperator.Set, Num.Zero, context);

            GameEvent moved = Assert.Single(recorder.Named("moved"));
            Assert.Equal(goon, moved.Target);
            Assert.Equal("actor", moved.Data["kind"].Text);
            Assert.Equal("floor 1, slot 2", moved.Data["from"].Text);
            Assert.Equal("floor 1, slot 0", moved.Data["to"].Text);
            Assert.Equal(2, moved.Data["from_rank"].Number.ToInt());
            Assert.Equal(0, moved.Data["to_rank"].Number.ToInt());
        }

        /// <summary>
        /// There is no permission system: a unit that must not be moved says so by refusing the
        /// event, which is knockback immunity written the day movement ships.
        /// </summary>
        [Fact]
        public void Before_moved_is_how_a_unit_refuses_to_be_moved()
        {
            CardRuntime runtime = Create(Line + """

                status "Rooted"
                  stacking none
                  on before_moved(target:owner):
                    cancel
                """);
            Start(runtime);

            Entity front = Goon(runtime, 0, 0);
            Entity rooted = Goon(runtime, 0, 1);
            Entity willing = Goon(runtime, 0, 2);
            runtime.Interpreter.ApplyStatus(runtime.Content.Find("Rooted", "status")!, rooted, Num.One, null, new EvalContext(rooted));

            runtime.Play(Card(runtime, "Haul"), rooted);
            Assert.Equal(1, rooted.Rank);
            Assert.Equal(0, front.Rank);

            // The same card on a unit with no objection, so that "nothing moved" is the status
            // refusing rather than movement not working.
            runtime.Play(Card(runtime, "Haul"), willing);
            Assert.Equal(0, willing.Rank);
            Assert.Equal(2, front.Rank);
        }

        /// <summary>
        /// A place is two integers and its inverse is exact, which is why `until` reverts a move
        /// where it refuses a `transform` (CT321): nothing remembers an old form, and a slot is
        /// remembered in a pair of numbers.
        /// </summary>
        [Fact]
        public void Until_puts_an_actor_back_where_it_stood()
        {
            CardRuntime runtime = Create(Line + """

                card "Brief Haul"
                  cost 0
                  target enemy
                  effect:
                    until turn_end:
                      target.rank = 0
                """);
            Start(runtime);

            Entity front = Goon(runtime, 0, 0);
            Entity behind = Goon(runtime, 0, 1);

            runtime.Play(Card(runtime, "Brief Haul"), behind);
            Assert.Equal(0, behind.Rank);
            Assert.Equal(1, front.Rank);

            runtime.EndTurn();
            Assert.Equal(1, behind.Rank);
            Assert.Equal(0, front.Rank);
        }

        /// <summary>
        /// A row closing is a move, and a game that animates one has to hear about it. It is told
        /// after the fact: the row has already closed, and refusing half of it would put two actors
        /// on one slot.
        /// </summary>
        [Fact]
        public void Close_ranks_says_moved_for_every_survivor_that_steps_forward()
        {
            var recorder = new Recorder();
            CardRuntime runtime = Create("""
                board Corridor
                  lanes 1
                  ranks 4
                  on_vacated close_ranks

                enemy "Goon"
                  hp 30
                  attack 4
                """, new RuntimeOptions { Host = recorder });
            Start(runtime);

            Entity first = runtime.State.Instantiate(runtime.Content.Find("Goon", "enemy")!, null, Team.Enemy, Zones.Board);
            Entity second = runtime.State.Instantiate(runtime.Content.Find("Goon", "enemy")!, null, Team.Enemy, Zones.Board);
            Entity third = runtime.State.Instantiate(runtime.Content.Find("Goon", "enemy")!, null, Team.Enemy, Zones.Board);
            Assert.Equal(new[] { 0, 1, 2 }, new[] { first.Rank, second.Rank, third.Rank });

            runtime.Interpreter.Kill(first, runtime.Player, new EvalContext(runtime.Player!));
            runtime.Interpreter.Drain();

            Assert.Equal(0, second.Rank);
            Assert.Equal(1, third.Rank);

            List<GameEvent> heard = recorder.Named("moved");
            Assert.Equal(2, heard.Count);
            Assert.Equal(new[] { second, third }, heard.Select(e => e.Target).ToArray());
            Assert.Equal("lane 0, rank 1", heard[0].Data["from"].Text);
            Assert.Equal("lane 0, rank 0", heard[0].Data["to"].Text);
        }
    }
}
