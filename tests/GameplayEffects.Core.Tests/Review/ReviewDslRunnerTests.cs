using System;
using System.Linq;
using GameplayEffects.Content;
using GameplayEffects.Testing;
using Xunit;
using static GameplayEffects.Tests.Review.ReviewSupport;

namespace GameplayEffects.Tests.Review
{
    /// <summary>
    /// The DSL test runner: failure reporting and the test-only verbs. Regression tests: each
    /// test's summary records a defect a review found, now fixed.
    /// </summary>
    public sealed class ReviewDslRunnerTests
    {
        private const string StrikeContent = @"
card ""Strike""
  cost 1
  target enemy
  effect:
    deal 6 to target

relic ""Kindling""
  on damaged:
    log ""warm""
";

        /// <summary>
        /// DslTestRunner.Run only catches TestFailure, RuntimeError and DslException. A typo in a
        /// card or relic name (ArgumentException from CardRuntime.Require) or <c>tick</c> without
        /// <c>realtime</c> (InvalidOperationException) escapes and aborts the whole run, CLI included,
        /// even though Require's message already carries a did-you-mean hint.
        /// </summary>
        [Theory]
        [Trait("Regression", "runner-lets-host-exceptions-escape")]
        [InlineData("play Strke on enemy")]
        [InlineData("hand Strke")]
        [InlineData("relic Kindlng")]
        [InlineData("tick 1")]
        public void A_bad_test_line_fails_that_test_instead_of_throwing(string line)
        {
            ContentLibrary content = LoadContent(StrikeContent + "\ntest \"bad line\"\n  enemy hp 10\n  " + line + "\n");
            var runner = new DslTestRunner(content);

            DslTestResult? result = null;
            Exception? error = Record.Exception(() => result = runner.Run(content.Tests.Single()));

            Assert.Null(error);
            Assert.False(result!.Passed);
        }

        /// <summary>
        /// The <c>enemy</c> test verb calls RollIntent after SpawnEnemy, which already rolled one
        /// during a battle. A cycling enemy added mid-test therefore skips its first move.
        /// </summary>
        [Fact]
        [Trait("Regression", "runner-rolls-intent-twice")]
        public void An_enemy_added_mid_battle_opens_with_its_first_move()
        {
            ContentLibrary content = LoadContent(@"
enemy ""Jaw Worm""
  hp 40
  move ""Chomp"":
    deal 11 to player
  move ""Bellow"":
    block 6
  pattern cycle Chomp, Bellow

test ""late Jaw Worm""
  end turn
  enemy ""Jaw Worm""
  end turn
  expect player.hp == 69
");

            DslTestResult result = new DslTestRunner(content).Run(content.Tests.Single());

            Assert.True(result.Passed, result.ToString());
        }

        /// <summary>
        /// Units only bind when written flush against the number. <c>for 3 seconds</c> parses as a
        /// bare 3 plus a stray argument <c>seconds</c> that nothing reads, so on a tick clock the
        /// status lasts 3 ticks instead of 3 seconds, with no diagnostic.
        /// </summary>
        [Fact]
        [Trait("Regression", "spaced-time-unit-ignored")]
        public void A_spaced_time_unit_is_honoured_or_rejected()
        {
            ContentLibrary content = ContentLibrary.FromText(@"
status ""Slow""
  stacking intensity

test ""three seconds""
  realtime 10
  enemy hp 10
  apply Slow 1 for 3 seconds to enemy
  tick 5
  expect enemy.Slow == 1
");
            if (content.Diagnostics.HasErrors) return; // rejecting the spaced unit is an acceptable fix

            DslTestResult result = new DslTestRunner(content).Run(content.Tests.Single());

            Assert.True(result.Passed, result.ToString());
        }
    }
}
