using System;
using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Runtime;
using Cantrip.Syntax;
using Cantrip.Testing;
using Xunit;
using static Cantrip.Tests.Review.ReviewSupport;

namespace Cantrip.Tests.Review
{
    /// <summary>
    /// DslTestRunner builds its own CardRuntime for each test, so a game had no way to give it the
    /// verbs and host its content uses, although the docs told readers to test such content
    /// "through DslTestRunner with the verb registered". <see cref="DslTestRunner.ConfigureRuntime"/>
    /// and <see cref="DslTestRunner.CreateHost"/> are that way.
    /// </summary>
    public sealed class RunnerHookTests
    {
        private const string Corruption = """
            card Taint
              cost 1
              target enemy
              effect:
                corrupt 3 to target

            test "Taint corrupts its target"
              enemy hp 20
              play Taint on enemy
              expect enemy.corruption == 3

            """;

        /// <summary>The verb a game registers in C#, as docs/csharp.md shows it.</summary>
        private static void RegisterCorrupt(CardRuntime runtime) =>
            runtime.RegisterVerb("corrupt", call =>
            {
                Num amount = call.Number(0, Num.One);
                foreach (Entity target in call.Targets("to"))
                    call.Interpreter.ChangeStat(target, "corruption", AssignOperator.Add, amount, call.Context, call.Span);
            });

        [Fact]
        [Trait("Regression", "runner-cannot-register-verbs")]
        public void A_content_test_that_uses_a_game_verb_passes_through_the_hook()
        {
            ContentLibrary content = LoadContent(Corruption);

            // Without the verb, the test cannot pass.
            DslTestResult without = new DslTestRunner(content).RunAll("Taint").Single();
            Assert.False(without.Passed);

            var runner = new DslTestRunner(content) { ConfigureRuntime = RegisterCorrupt };
            DslTestResult with = runner.RunAll("Taint").Single();
            Assert.True(with.Passed, with.ToString());
        }

        [Fact]
        public void Each_test_gets_a_fresh_runtime_through_the_hook()
        {
            ContentLibrary content = LoadContent(Corruption + """
                test "a second test"
                  enemy hp 20
                  play Taint on enemy
                  play Taint on enemy
                  expect enemy.corruption == 6
                """);

            var configured = new List<CardRuntime>();
            var hosts = new List<IEffectHost>();
            var runner = new DslTestRunner(content)
            {
                CreateHost = () =>
                {
                    var host = new EffectHostBase();
                    hosts.Add(host);
                    return host;
                },
                ConfigureRuntime = runtime =>
                {
                    Assert.Null(runtime.Player);
                    Assert.Same(hosts[hosts.Count - 1], runtime.Interpreter.Host);
                    RegisterCorrupt(runtime);
                    configured.Add(runtime);
                },
            };

            IReadOnlyList<DslTestResult> results = runner.RunAll();
            Assert.All(results, r => Assert.True(r.Passed, r.ToString()));
            Assert.Equal(2, configured.Count);
            Assert.NotSame(configured[0], configured[1]);
            Assert.Equal(2, hosts.Count);
            Assert.NotSame(hosts[0], hosts[1]);
        }

        private sealed class DoomHost : EffectHostBase
        {
            public override bool TryResolveName(string name, EvalContext context, out Value value)
            {
                if (name == "doom")
                {
                    value = Value.FromNumber(Num.FromInt(7));
                    return true;
                }
                return base.TryResolveName(name, context, out value);
            }
        }

        [Fact]
        public void A_name_the_game_answers_resolves_through_the_host()
        {
            ContentLibrary content = LoadContent("""
                card Omen
                  cost 0
                  target enemy
                  effect:
                    deal doom to target

                test "Omen deals doom"
                  enemy hp 20
                  play Omen on enemy
                  expect enemy.hp == 13
                """);

            Assert.False(new DslTestRunner(content).RunAll().Single().Passed);

            var hosts = new List<DoomHost>();
            var runner = new DslTestRunner(content)
            {
                CreateHost = () =>
                {
                    var host = new DoomHost();
                    hosts.Add(host);
                    return host;
                },
            };

            DslTestResult result = runner.RunAll().Single();
            Assert.True(result.Passed, result.ToString());
            Assert.Single(hosts);
        }

        /// <summary>The host from docs/csharp.md, which records events for presentation.</summary>
        private sealed class GameHost : EffectHostBase
        {
            public List<GameEvent> Events { get; } = new List<GameEvent>();

            public override void OnEvent(GameEvent gameEvent) => Events.Add(gameEvent);
        }

        /// <summary>The verb from docs/csharp.md, kept in a method so a game and its tests register the same one.</summary>
        private static void Corrupt(VerbCall call)
        {
            Num amount = call.Number(0, Num.One);
            foreach (Entity target in call.Targets("to"))
            {
                Num added = call.Interpreter.ChangeStat(target, "corruption", AssignOperator.Add, amount, call.Context, call.Span);
                call.Interpreter.Raise(new GameEvent("corrupted") { Source = call.Context.Source, Target = target, Amount = added }, call.Context);
            }
        }

        /// <summary>The example docs/csharp.md gives for testing content that uses a game's verb.</summary>
        [Fact]
        public void The_csharp_guide_example_runs()
        {
            ContentLibrary content = LoadContent(Corruption);

            var runner = new DslTestRunner(content)
            {
                CreateHost = () => new GameHost(),
                ConfigureRuntime = runtime => runtime.RegisterVerb("corrupt", Corrupt),
            };
            foreach (DslTestResult result in runner.RunAll())
                Assert.True(result.Passed, result.ToString());
        }

        [Fact]
        public void A_player_the_hook_creates_is_the_one_the_test_uses()
        {
            ContentLibrary content = LoadContent("""
                test "the game's own player"
                  expect player.hp == 50
                  expect player.max_energy == 4
                """);

            var runner = new DslTestRunner(content) { ConfigureRuntime = runtime => runtime.CreatePlayer(hp: 50, maxEnergy: 4) };
            DslTestResult result = runner.RunAll().Single();
            Assert.True(result.Passed, result.ToString());
        }

        [Fact]
        public void A_failing_hook_fails_the_test_with_its_message()
        {
            ContentLibrary content = LoadContent(Corruption);
            var runner = new DslTestRunner(content) { ConfigureRuntime = _ => throw new InvalidOperationException("no save slot") };

            DslTestResult result = runner.RunAll("Taint").Single();
            Assert.False(result.Passed);
            Assert.Equal("could not configure the runtime: no save slot", result.Failure);
        }

        /// <summary>
        /// .NET appends "(Parameter 'name')" to an ArgumentException's message. A game verb's own
        /// complaint, like the runtime's, reaches the test's failure without it.
        /// </summary>
        [Fact]
        [Trait("Regression", "runner-shows-parameter-suffix")]
        public void An_argument_exception_fails_the_test_without_the_parameter_suffix()
        {
            ContentLibrary content = LoadContent(Corruption);
            var runner = new DslTestRunner(content)
            {
                ConfigureRuntime = runtime => runtime.RegisterVerb("corrupt", call => throw new ArgumentException("Corruption needs a living target.", "target")),
            };

            DslTestResult result = runner.RunAll("Taint").Single();
            Assert.False(result.Passed);
            Assert.Equal("Corruption needs a living target.", result.Failure);
        }
    }
}
