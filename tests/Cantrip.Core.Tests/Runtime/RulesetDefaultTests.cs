#nullable enable
using System.Reflection;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// A fresh ruleset is a method call, not a property. As a property it read like a shared
    /// settings object, so <c>Ruleset.Default.HandSize = 99</c> set a field on an instance nobody
    /// kept and was silently discarded — while the C# guide tells games to set ruleset properties.
    /// </summary>
    public sealed class RulesetDefaultTests
    {
        [Fact]
        public void There_is_no_property_that_looks_like_a_shared_ruleset()
        {
            Assert.Null(typeof(Ruleset).GetProperty("Default", BindingFlags.Public | BindingFlags.Static));
            Assert.NotNull(typeof(Ruleset).GetMethod("CreateDefault", BindingFlags.Public | BindingFlags.Static));
        }

        [Fact]
        public void Each_call_hands_back_a_ruleset_of_its_own()
        {
            Ruleset mine = Ruleset.CreateDefault();
            int printed = mine.HandSize;

            mine.HandSize = 99;

            Assert.NotSame(mine, Ruleset.CreateDefault());
            Assert.Equal(printed, Ruleset.CreateDefault().HandSize);
            Assert.Equal(99, mine.HandSize);
        }

        [Fact]
        public void A_ruleset_the_game_caps_is_the_one_the_runtime_uses()
        {
            Ruleset rules = Ruleset.CreateDefault();
            rules.HandSize = 3;

            CardRuntime runtime = CardRuntime.FromText(
                "card \"Strike\"\n  cost 0\n  effect:\n    block 1\n", new RuntimeOptions { Rules = rules });
            runtime.CreatePlayer();
            for (int i = 0; i < 6; i++) runtime.AddCard("Strike");
            runtime.StartBattle(shuffle: false);

            Assert.Equal(3, runtime.State.ZoneOf(runtime.Player, Zones.Hand).Count);
        }
    }
}
