using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Cantrip.Testing;
using Xunit;

namespace Cantrip.Tests.Runtime
{
    /// <summary>
    /// <c>new_listeners</c>: whether a listener that comes into play during an event hears that
    /// event.
    /// </summary>
    /// <remarks>
    /// Both answers are defensible, which is why this is a setting and not a fix. A minion summoned
    /// by "whenever you summon a minion" hearing its own summoning is a bug in most games, and the
    /// workaround is a filter on every listener of that shape. But it is a real card in others, and
    /// the sample roguelite's Chill is written against it, so the default stays where it was and
    /// content that cares says so.
    /// </remarks>
    public sealed class NewListenerTests
    {
        private const string Summons = """
            actor "Juggler"
              hp 2
              on created(kind:actor):
                deal 1 to enemies.first

            card "Muster"
              cost 0
              effect:
                create Juggler

            """;

        /// <summary>The default is today's behaviour: the Juggler hears its own summoning.</summary>
        [Fact]
        public void By_default_a_new_listener_hears_the_event_that_brought_it_in() => Passes(Summons + """
            test "the Juggler throws a knife for itself"
              enemy hp 20
              hand Muster
              player energy 9
              play Muster
              expect enemy.hp == 19
            """);

        [Fact]
        [Trait("Regression", "listener-hears-its-own-cause")]
        public void A_ruleset_can_say_that_it_misses_it() => Passes("""
            ruleset
              new_listeners: miss_the_event

            """ + Summons + """
            test "the Juggler does not throw a knife for itself"
              enemy hp 20
              hand Muster
              player energy 9
              play Muster
              expect enemy.hp == 20

            test "but it does throw one for the next minion"
              enemy hp 20
              hand Muster, Muster
              player energy 9
              play Muster
              expect enemy.hp == 20
              play Muster
              expect enemy.hp == 19
            """);

        /// <summary>
        /// The workaround the setting replaces still works, and must: content that already writes
        /// `not target:self` is unaffected whichever way the setting is set.
        /// </summary>
        [Fact]
        public void The_filter_that_says_it_by_hand_still_works() => Passes("""
            actor "Juggler"
              hp 2
              on created(kind:actor, not target:self):
                deal 1 to enemies.first

            card "Muster"
              cost 0
              effect:
                create Juggler

            test "leaving its own summon out"
              enemy hp 20
              hand Muster, Muster
              player energy 9
              play Muster
              expect enemy.hp == 20
              play Muster
              expect enemy.hp == 19
            """);

        [Fact]
        public void The_setting_reads_back_off_the_ruleset()
        {
            Assert.Equal(NewListeners.HearTheEvent, Ruleset.CreateDefault().NewListeners);

            ContentLibrary content = ContentLibrary.FromText("ruleset\n  new_listeners: miss_the_event\n", "ruleset.cantrip");
            Assert.Equal(NewListeners.MissTheEvent, content.BuildRuleset().NewListeners);

            // Two rulesets that differ here are not the same ruleset, which is what a hot reload
            // needs in order to say that a running game is resolving against the old rules.
            Assert.False(Ruleset.CreateDefault().SameAs(content.BuildRuleset()));
        }

        [Fact]
        public void An_unknown_value_is_reported_rather_than_guessed()
        {
            ContentLibrary content = ContentLibrary.FromText("ruleset\n  new_listeners: sometimes\n", "ruleset.cantrip");
            var diagnostics = new DiagnosticBag();
            content.BuildRuleset(diagnostics);

            Diagnostic error = Assert.Single(diagnostics.Errors);
            Assert.Equal("CT0202", error.Code);
            Assert.Contains("Write `hear_the_event` or `miss_the_event`", error.Message);
        }

        // Helpers ----------------------------------------------------------------------------

        private static void Passes(string dsl)
        {
            ContentLibrary content = ContentLibrary.FromText(dsl, "new-listeners.cantrip");
            Assert.False(content.Diagnostics.HasErrors, content.Diagnostics.ToString());

            IReadOnlyList<DslTestResult> results = new DslTestRunner(content).RunAll();
            Assert.NotEmpty(results);
            foreach (DslTestResult result in results) Assert.True(result.Passed, result.ToString());
        }
    }
}
