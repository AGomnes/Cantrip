using Cantrip.Content;
using Cantrip.Descriptions;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// Content for the view mappings: a card whose numbers a relic changes, statuses that count in
    /// the two different ways, a hidden one, and an enemy with an intent to describe.
    /// </summary>
    /// <remarks>
    /// Written against a real game rather than stand-in objects, because what is being tested is
    /// whether the mapping tells the truth about the core's own numbers.
    /// </remarks>
    internal static class ViewTestKit
    {
        public const string Content = @"card ""Strike""
  cost 1
  target enemy
  tags attack
  effect:
    deal 6 to target

card ""Fireball""
  cost 2
  target enemy
  tags attack, fire
  effect:
    deal 6 to target
    apply Burn 2 to target
  text: ""Hurl a ball of flame for {damage} damage.""
  flavour: ""Warm regards.""

card ""Footnote""
  cost 0
  tags skill
  effect:
    draw 1
  text_override: ""Draw a card [see the rules].""

relic ""Pyromancer's Codex""
  modify damage where tag:fire, source:self: x1.5
  modify cost of cards where tag:fire: -1

status ""Burn""
  tags dot, fire, debuff
  stacking intensity
  on turn_end:
    deal stacks to owner, ignore block

status ""Strength""
  tags buff
  stacking intensity
  modify damage: +stacks

status ""Vulnerable""
  tags debuff
  stacking duration
  modify damage_taken: x1.5

status ""Marked""
  flags hidden
  stacking intensity

enemy ""Jaw Worm""
  hp 40
  move ""Chomp"":
    deal 11 to player
  pattern cycle Chomp
";

        public static ContentLibrary Library()
        {
            ContentLibrary library = ContentLibrary.FromText(Content, "res://content/test.cantrip");
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());
            return library;
        }

        /// <summary>A runtime with a player and one Jaw Worm, before the battle starts.</summary>
        public static CardRuntime Runtime(out Entity enemy)
        {
            var runtime = new CardRuntime(Library(), new RuntimeOptions { Seed = 1 });
            runtime.CreatePlayer();
            enemy = runtime.SpawnEnemy("Jaw Worm");
            return runtime;
        }

        public static DescriptionBuilder Descriptions(CardRuntime runtime) => new DescriptionBuilder(runtime.Content);
    }
}
