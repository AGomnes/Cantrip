using System.Collections.Generic;
using System.Linq;
using Cantrip.Content;
using Cantrip.Diagnostics;
using Cantrip.Runtime;
using Xunit;

namespace Cantrip.GodotAdapter.Tests.Shared
{
    /// <summary>
    /// What the editor can ask a running game, and what it must be told back. All of it without an
    /// engine: the awkward part of a debug channel is deciding what to send, not the sending.
    /// </summary>
    public sealed class CantripDebugServiceTests
    {
        private const string File = "res://content/cards.cantrip";

        private const string Content = @"card ""Ember""
  cost 1
  target enemy
  effect:
    deal 5 to target

enemy ""Slime""
  hp 30
  move ""Swipe"":
    deal 4 to player
  pattern cycle Swipe
";

        private static CantripDebugService Started(out Entity slime)
        {
            ContentLibrary library = ContentLibrary.FromText(Content, File);
            Assert.False(library.Diagnostics.HasErrors, library.Diagnostics.ToString());

            var runtime = new CardRuntime(library, new RuntimeOptions { Seed = 1 });
            runtime.CreatePlayer();
            slime = runtime.SpawnEnemy("Slime");
            runtime.AddCard("Ember", Zones.Hand);
            runtime.StartBattle(shuffle: false, drawOpeningHand: false);
            return new CantripDebugService(runtime);
        }

        [Fact]
        public void A_game_introduces_itself()
        {
            CantripDebugService service = Started(out Entity _);

            CantripHello hello = service.Hello();

            Assert.Equal(CantripProtocol.Version, hello.Protocol);
            Assert.Equal(2, hello.Definitions);
            Assert.Equal(service.Content.Fingerprint, hello.Fingerprint);
            Assert.True(hello.InBattle);
            Assert.Equal(1, hello.Turn);
            Assert.False(hello.Tracing);
        }

        [Fact]
        public void Tracing_is_off_until_the_editor_asks_for_it()
        {
            CantripDebugService service = Started(out Entity slime);

            service.Runtime.Execute("deal 3 to enemy", target: slime);
            Assert.Empty(service.Fetch().Entries);

            service.EnableTrace(true, 500);
            Assert.True(service.Hello().Tracing);

            service.Runtime.Execute("deal 3 to enemy", target: slime);
            TraceBatch batch = service.Fetch();
            Assert.NotEmpty(batch.Entries);
            Assert.Contains(batch.Entries, e => e.Kind == "event" || e.Kind == "verb");

            // Turning it off clears what was collected, so a long session costs nothing afterwards.
            service.EnableTrace(false, 500);
            Assert.Empty(service.Fetch().Entries);
        }

        [Fact]
        public void The_cursor_only_brings_back_what_is_new()
        {
            CantripDebugService service = Started(out Entity slime);
            service.EnableTrace(true, 500);

            service.Runtime.Execute("deal 1 to enemy", target: slime);
            TraceBatch first = service.Fetch();
            Assert.NotEmpty(first.Entries);

            Assert.Empty(service.Fetch(first.NextId).Entries);

            service.Runtime.Execute("deal 1 to enemy", target: slime);
            TraceBatch second = service.Fetch(first.NextId);
            Assert.NotEmpty(second.Entries);
            Assert.True(second.Entries[0].Id > first.NextId);
        }

        [Fact]
        public void Saving_a_file_changes_the_running_game()
        {
            CantripDebugService service = Started(out Entity slime);

            CantripReloadResult result = service.Reload(new[]
            {
                new KeyValuePair<string, string>(File, Content.Replace("deal 5 to target", "deal 9 to target")),
            });

            Assert.True(result.Applied, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            Assert.True(result.Rebound > 0);
            Assert.Empty(result.Missing);
            Assert.False(result.RulesetChanged);

            Assert.Equal(PlayResult.Played, service.Runtime.Play("Ember", slime));
            Assert.Equal(21, slime.GetInt("hp"));
        }

        [Fact]
        public void A_file_that_no_longer_parses_is_reported_but_not_applied()
        {
            CantripDebugService service = Started(out Entity slime);

            // A limit with nothing after it: the parser stops here, so the file contributes no
            // definitions at all. That is the case worth refusing, because applying it would strip
            // a running game of the cards it is mid-way through playing.
            CantripReloadResult result = service.Reload(new[]
            {
                new KeyValuePair<string, string>(File, "card \"Ember\"\n  cost 1\n  on card_played once:\n    draw 1\n"),
            });

            Assert.False(result.Applied);
            Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Equal(0, result.Rebound);

            // The game carries on with the definitions it already had, mid-battle and unharmed.
            Assert.True(service.Runtime.State.InBattle);
            Assert.Equal(30, slime.GetInt("hp"));
        }

        [Fact]
        public void The_console_runs_statements_and_reports_what_fails()
        {
            CantripDebugService service = Started(out Entity slime);

            CantripExecuteResult ok = service.Execute("deal 4 to enemy");
            Assert.True(ok.Ok, ok.Message);
            Assert.Equal(26, slime.GetInt("hp"));

            CantripExecuteResult nonsense = service.Execute("deal 4 to whatever");
            Assert.False(nonsense.Ok);
            Assert.NotEqual(string.Empty, nonsense.Message);

            // A failed console line leaves the game as it was.
            Assert.Equal(26, slime.GetInt("hp"));
            Assert.True(service.Runtime.State.InBattle);

            Assert.False(service.Execute("   ").Ok);
        }
    }
}
