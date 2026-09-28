#nullable enable
using System;
using System.Collections.Generic;
using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// The four seams a game implements — <see cref="IEffectHost"/>, <see cref="IGameClock"/>,
    /// <see cref="IChoiceProvider"/> and <see cref="IDescriptionLocalizer"/> — give their members
    /// default implementations, so a game writes only the part it has an opinion about and a member
    /// added in a later release cannot break it. These pin the defaults themselves: each type here
    /// implements the smallest thing the compiler will accept.
    /// </summary>
    public sealed class InterfaceDefaultTests
    {
        private const string Content = """
            enemy "Dummy"
              hp 40

            status "Weak"
              tags debuff

            card "Strike"
              cost 0
              target enemy
              effect:
                deal 6 to target

            card "Hex"
              cost 0
              target enemy
              effect:
                apply Weak 1 to target for 2
            """;

        /// <summary>Names no member at all. Before the defaults existed this would not compile.</summary>
        private sealed class EmptyHost : IEffectHost
        {
        }

        private sealed class EmptyLocalizer : IDescriptionLocalizer
        {
        }

        /// <summary>Only what no clock can default: where it is, when it moves, and how to put it back.</summary>
        private sealed class BareClock : IGameClock
        {
            public long Now { get; private set; }

            public event Action<long>? Advanced;

            public void Advance()
            {
                Now++;
                Advanced?.Invoke(Now);
            }

            public void Restore(long now) => Now = now;
        }

        [Fact]
        public void A_host_that_implements_nothing_runs_a_battle()
        {
            var host = new EmptyHost();
            CardRuntime runtime = CardRuntime.FromText(Content, new RuntimeOptions { Seed = 1, Host = host });
            Entity player = runtime.CreatePlayer();
            Entity enemy = runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Strike", Zones.Hand);
            Assert.Equal(ActionResult.Played, runtime.StartBattle(shuffle: false, drawOpeningHand: false));

            Assert.Equal(ActionResult.Played, runtime.Play("Strike", enemy));
            Assert.Equal(34, enemy.GetInt("hp"));
            Assert.True(player.IsAlive);
        }

        [Fact]
        public void A_hosts_defaults_answer_no_to_a_name_and_a_call()
        {
            IEffectHost host = new EmptyHost();
            var context = new EvalContext(null);

            Assert.False(host.TryResolveName("anything", context, out Value name));
            Assert.Equal(Value.None, name);

            Assert.False(host.TryCall("within", new List<Value>(), context, out Value call));
            Assert.Equal(Value.None, call);

            // And the one that returns nothing is a no-op rather than a missing method.
            host.OnEvent(new GameEvent("damaged"));
        }

        [Fact]
        public void A_clocks_default_takes_its_own_unit_and_refuses_every_named_one()
        {
            IGameClock clock = new BareClock();

            Assert.True(clock.TryConvert(Num.FromInt(3), null, out long bare));
            Assert.Equal(3, bare);

            Assert.True(clock.TryConvert(Num.FromInt(2), string.Empty, out long empty));
            Assert.Equal(2, empty);

            Assert.False(clock.TryConvert(Num.FromInt(3), "s", out long seconds));
            Assert.Equal(0, seconds);
            Assert.False(clock.TryConvert(Num.FromInt(3), "turns", out _));
        }

        [Fact]
        public void A_clock_that_only_says_where_it_is_drives_a_duration()
        {
            var clock = new BareClock();
            CardRuntime runtime = CardRuntime.FromText(Content, new RuntimeOptions { Seed = 1, Clock = clock });
            runtime.CreatePlayer();
            Entity enemy = runtime.SpawnEnemy("Dummy");
            runtime.AddCard("Hex", Zones.Hand);
            Assert.Equal(ActionResult.Played, runtime.StartBattle(shuffle: false, drawOpeningHand: false));

            // `for 2` is two of this clock's own units, which is what the default conversion says.
            Assert.Equal(ActionResult.Played, runtime.Play("Hex", enemy));
            Assert.NotNull(enemy.FindAttached("Weak"));

            clock.Advance();
            Assert.NotNull(enemy.FindAttached("Weak"));

            clock.Advance();
            Assert.Null(enemy.FindAttached("Weak"));
        }

        [Fact]
        public void A_localizer_that_translates_nothing_reads_as_the_built_in_English()
        {
            ContentLibrary content = ContentLibrary.FromText(Content);
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());

            EntityDefinition strike = content.Find("Strike", "card")!;
            string plain = new DescriptionBuilder(content).Describe(strike).ToPlainText();
            string through = new DescriptionBuilder(content, new EmptyLocalizer()).Describe(strike).ToPlainText();

            Assert.Equal(plain, through);
            Assert.Contains("6", through);
        }

        [Fact]
        public void Every_member_of_a_localizer_defaults_to_no_translation()
        {
            ContentLibrary content = ContentLibrary.FromText(Content);
            EntityDefinition strike = content.Find("Strike", "card")!;
            IDescriptionLocalizer localizer = new EmptyLocalizer();

            Assert.Null(localizer.Name(strike));
            Assert.Null(localizer.Text(strike));
            Assert.Null(localizer.Flavour(strike));
            Assert.Null(localizer.Phrase("deal"));
        }
    }
}
