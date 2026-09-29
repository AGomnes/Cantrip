using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// <c>fallen</c>: the dead of a side, beside <c>party</c> and <c>allies</c>.
    /// </summary>
    /// <remarks>
    /// Nothing could name a corpse before this word, so <c>revive</c> — a verb that exists because
    /// <c>heal</c> refuses a dead target, deliberately and permanently — had no argument content
    /// could write. <c>target ally</c> and <c>target any</c> both want somebody living,
    /// <c>allies</c> and <c>party</c> leave the dead out by design, and
    /// <c>everyone where zone:dead</c> binds nobody. The in-combat raise every party roguelite ships
    /// could not be spelt at all, in a language that had the verb for it.
    /// </remarks>
    public sealed class FallenTests
    {
        private const string Content = """
            hero "Warden"
              hp 30

            hero "Cantor"
              hp 20
              abilities LastRites

            ability "LastRites"
              cooldown 2 turns
              effect:
                revive fallen.first 8

            card "Raise"
              cost 0
              effect:
                choose 1 from fallen as who
                revive who 5

            enemy "Dummy"
              hp 100
            """;

        private static CardRuntime Party(out Entity warden, out Entity cantor)
        {
            CardRuntime runtime = CardRuntime.FromText(Content);
            runtime.CreatePlayer();
            warden = runtime.AddHero("Warden");
            cantor = runtime.AddHero("Cantor");
            runtime.SpawnEnemy("Dummy");
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return runtime;
        }

        [Fact]
        public void The_dead_of_a_side_are_in_fallen_and_nowhere_else()
        {
            CardRuntime runtime = Party(out Entity warden, out _);
            Assert.Empty(runtime.Fallen);

            runtime.Execute("kill target", target: warden);

            Assert.Equal(new[] { warden }, runtime.Fallen);
            Assert.Equal(new[] { warden }, runtime.State.Fallen(Team.Player));
            Assert.DoesNotContain(warden, runtime.Party);
            Assert.DoesNotContain(warden, runtime.State.Actors());
        }

        [Fact]
        public void And_the_two_sides_are_told_apart()
        {
            CardRuntime runtime = Party(out Entity warden, out _);
            Entity dummy = runtime.State.Actors(Team.Enemy)[0];

            runtime.Execute("kill target", target: warden);
            runtime.Execute("kill target", target: dummy);

            Assert.Equal(new[] { warden }, runtime.State.Fallen(Team.Player));
            Assert.Equal(new[] { dummy }, runtime.State.Fallen(Team.Enemy));
            Assert.Equal(2, runtime.State.Fallen().Count);
        }

        [Fact]
        public void Content_can_name_the_fallen_and_raise_one()
        {
            Passes("""
                test "an ability raises the fallen"
                  player hp 40
                  hero Warden
                  hero Cantor
                  enemy "Dummy"
                  expect count(fallen) == 0
                  kill Warden
                  expect count(fallen) == 1
                  expect fallen.first.name == "Warden"
                  expect count(party) == 2
                  cast LastRites by Cantor
                  expect Warden.hp == 8
                  expect count(fallen) == 0

                test "and a card can choose from them"
                  player hp 40
                  hero Warden
                  hero Cantor
                  hand Raise
                  enemy "Dummy"
                  kill Warden
                  play Raise
                  expect Warden.hp == 5
                """);
        }

        private static void Passes(string tests)
        {
            ContentLibrary content = ContentLibrary.FromText(Content + "\n" + tests, "fallen.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());

            IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }
    }
}
