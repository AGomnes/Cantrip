using System;
using System.Collections.Generic;
using Godot;

namespace GameplayEffects.GodotAdapter.Demo
{
    /// <summary>
    /// Drives the addon the way a game does, inside a real Godot process, and exits with a status
    /// code so CI can gate on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine-free parts of the adapter are covered by xunit, which needs no Godot at all. What
    /// cannot be covered there is everything this file exercises: that the node marshals across the
    /// Variant boundary, that signals arrive in resolution order, that content loads through
    /// <c>res://</c>, and that a deferred choice survives the round trip.
    /// </para>
    /// <para>
    /// Exit codes: 0 every check passed, 1 a check failed, 2 the harness itself threw. The third
    /// matters because Godot otherwise swallows an exception and exits 0, which reads as success.
    /// </para>
    /// </remarks>
    public partial class HeadlessTests : Node
    {
        private const string ContentFolder = "res://content";

        private readonly List<string> _failures = new List<string>();
        private readonly List<string> _events = new List<string>();
        private int _checks;
        private Godot.Collections.Dictionary _lastChoice = new Godot.Collections.Dictionary();

        public override void _Ready() => CallDeferred(nameof(RunAll));

        private void RunAll()
        {
            int code;
            try
            {
                ContentLoads();
                ABattlePlaysOut();
                EventsArriveAfterTheAction();
                ViewsAndDescriptionsTellTheTruth();
                ADeferredChoiceRollsBackAndReplays();
                SavingAndLoadingReturnsTheSameGame();
                HotReloadChangesARunningGame();
                AChoiceIsAnsweredFromTheSignalThatAsks();
                AHostCallbackCannotCallBackIn();
                TheDebugChannelAnswersTheEditor();

                code = _failures.Count == 0 ? 0 : 1;
            }
            catch (Exception error)
            {
                GD.PushError("Gameplay Effects headless tests: the harness threw. " + error);
                GD.Print("HEADLESS: harness error: " + error.Message);
                code = 2;
            }

            GD.Print($"HEADLESS: {_checks - _failures.Count}/{_checks} checks passed");
            foreach (string failure in _failures) GD.Print("HEADLESS FAIL: " + failure);
            GD.Print(code == 0 ? "HEADLESS: all checks passed" : "HEADLESS: failures");

            GetTree().Quit(code);
        }

        // Cases ------------------------------------------------------------------------------------

        private void ContentLoads()
        {
            GameplayEffectsRuntime rules = NewRuntime();

            Godot.Collections.Array problems = rules.LoadContent(ContentFolder);
            Check("content loads with no errors", Errors(problems) == 0, Describe(problems));
            Check("content has the demo's definitions", rules.Content.Find("Ember", "card") != null && rules.Content.Find("Slime", "enemy") != null);
            Check("test blocks came with it", rules.Content.Tests.Count >= 3, rules.Content.Tests.Count + " test block(s)");

            rules.QueueFree();
        }

        private void ABattlePlaysOut()
        {
            GameplayEffectsRuntime rules = Loaded();

            int player = rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            Check("the player and the enemy exist", player != 0 && slime != 0);

            rules.StartBattle(false, false);
            Check("the battle is running", rules.IsInBattle());
            Check("the enemy rolled an intent", rules.DescribeIntent(slime)["name"].AsString() == "Swipe");

            int ember = rules.AddCard("Ember", "hand");
            Check("Ember is playable", rules.CanPlay(ember));
            Check("Ember costs 1", rules.CostOf(ember) == 1);
            Check("Ember asks for an enemy", rules.GetTargetMode(ember) == "enemy");
            Check("the slime is a legal target", rules.GetLegalTargets(ember).Contains(slime));

            string result = rules.Play(ember, slime);
            Check("Ember played", result == "played", result);
            Check("the slime took 5", rules.GetStat(slime, "hp") == 25, rules.GetStat(slime, "hp").ToString());

            rules.EndTurn();
            Check("Burn ticked and the slime hit back", rules.GetStat(slime, "hp") == 23 && rules.GetStat(player, "hp") == 76,
                $"slime {rules.GetStat(slime, "hp")}, player {rules.GetStat(player, "hp")}");

            rules.QueueFree();
        }

        private void EventsArriveAfterTheAction()
        {
            GameplayEffectsRuntime rules = Loaded();
            _events.Clear();
            rules.EffectEvent += OnEffectEvent;

            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            _events.Clear();
            int ember = rules.AddCard("Ember", "hand");
            rules.Play(ember, slime);

            Check("the game heard the card being played", _events.Contains("card_played"), string.Join(", ", _events));
            Check("and the damage", _events.Contains("damaged"));
            Check("and the status", _events.Contains("status_applied"));
            // Events are told in completion order, so an event that wraps others is told last: the
            // damage resolves inside the card being played and therefore finishes before it does.
            Check("innermost first, because an event is told when it completes",
                _events.IndexOf("damaged") < _events.IndexOf("card_played"), string.Join(", ", _events));

            rules.EffectEvent -= OnEffectEvent;
            rules.QueueFree();
        }

        private void ViewsAndDescriptionsTellTheTruth()
        {
            GameplayEffectsRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);
            int ember = rules.AddCard("Ember", "hand");
            rules.Play(ember, slime);

            Godot.Collections.Dictionary view = rules.GetEntity(slime);
            Check("the entity view carries its stats", view["stats"].AsGodotDictionary()["hp"].AsInt32() == 25);
            Check("and its statuses", Status(view, "Burn") == 2, Status(view, "Burn").ToString());
            Check("and reads as an enemy actor", view["kind"].AsString() == "actor" && view["team"].AsString() == "enemy");

            int guard = rules.AddCard("Guard", "hand");
            Godot.Collections.Dictionary described = rules.Describe(guard);
            Check("a card describes itself", described["plain"].AsString() == "Gain 6 Block.", described["plain"].AsString());

            Godot.Collections.Dictionary intent = rules.DescribeIntent(slime);
            Check("an intent describes the move", intent["plain"].AsString() == "Deal 4 damage to the player.", intent["plain"].AsString());

            rules.QueueFree();
        }

        private void ADeferredChoiceRollsBackAndReplays()
        {
            GameplayEffectsRuntime rules = Loaded();
            _lastChoice = new Godot.Collections.Dictionary();
            rules.ChoiceRequested += OnChoiceRequested;

            rules.CreatePlayer();
            rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            int keep = rules.AddCard("Guard", "hand");
            int spare = rules.AddCard("Ember", "hand");
            int sort = rules.AddCard("Sort", "hand");

            string pending = rules.Play(sort);
            Check("a card needing a decision reports it", pending == "pending", pending);
            Check("the game was told what to ask", _lastChoice.Count > 0 && _lastChoice["prompt"].AsString().Length > 0);
            Check("with the cards to choose from", _lastChoice["option_ids"].AsGodotArray().Contains(keep));
            Check("and nothing happened yet", rules.GetHand().Contains(sort) && rules.GetZone(0, "exhaust").Count == 0);

            int requestId = _lastChoice["id"].AsInt32();
            Godot.Collections.Dictionary answered = rules.AnswerChoice(requestId, new Godot.Collections.Array { spare });
            Check("the answer was accepted", answered["accepted"].AsBool(), answered["message"].AsString());
            Check("and the action finished", answered["result"].AsString() == "played", answered["result"].AsString());
            Check("the chosen card was exhausted", rules.GetZone(0, "exhaust").Contains(spare));
            Check("nothing is pending now", !rules.HasPendingChoice());

            rules.ChoiceRequested -= OnChoiceRequested;
            rules.QueueFree();
        }

        private void SavingAndLoadingReturnsTheSameGame()
        {
            GameplayEffectsRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);
            int ember = rules.AddCard("Ember", "hand");

            Check("a game between actions can be saved", rules.CanSave());
            string save = rules.Save();
            string before = rules.StateHash();

            rules.Play(ember, slime);
            Check("playing changed the game", rules.StateHash() != before);

            Godot.Collections.Dictionary loaded = rules.LoadSave(save);
            Check("the save was accepted", loaded["accepted"].AsBool(), loaded["message"].AsString());
            Check("and the game is back where it was", rules.StateHash() == before, rules.StateHash() + " vs " + before);

            Godot.Collections.Dictionary refused = rules.LoadSave("{\"format\":1,\"fingerprint\":\"not-this-content\",\"snapshot\":\"{}\"}");
            Check("a save from other content is refused, not crashed into", !refused["accepted"].AsBool());
            Check("with a reason worth showing", refused["reason"].AsString() == "content_changed", refused["reason"].AsString());

            rules.QueueFree();
        }

        private void HotReloadChangesARunningGame()
        {
            GameplayEffectsRuntime rules = Loaded();
            rules.CreatePlayer();
            rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            Godot.Collections.Dictionary report = rules.ReloadContent();
            Check("reloading rebinds what is live", report["rebound"].AsInt32() > 0, report["rebound"].AsInt32().ToString());
            Check("and nothing went missing", report["missing"].AsGodotArray().Count == 0);
            Check("and the ruleset is unchanged", !report["ruleset_changed"].AsBool());

            rules.QueueFree();
        }

        /// <summary>
        /// The flow the documentation shows: a game is asked for a decision and answers it from the
        /// signal. This has to work, or deferred choices are unusable from a normal Godot game.
        /// </summary>
        private void AChoiceIsAnsweredFromTheSignalThatAsks()
        {
            GameplayEffectsRuntime rules = Loaded();
            rules.CreatePlayer();
            rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            int spare = rules.AddCard("Ember", "hand");
            int sort = rules.AddCard("Sort", "hand");

            string answered = "never asked";
            Action<Godot.Collections.Dictionary> answer = request =>
                answered = rules.AnswerChoice(request["id"].AsInt32(), new Godot.Collections.Array { spare })["result"].AsString();

            rules.ChoiceRequested += answer.Invoke;
            string played = rules.Play(sort);
            rules.ChoiceRequested -= answer.Invoke;

            Check("the card reported that it needed a decision", played == "pending", played);
            Check("answering from the signal finished the action", answered == "played", answered);
            Check("and the chosen card was exhausted", rules.GetZone(0, "exhaust").Contains(spare));
            Check("with nothing left pending", !rules.HasPendingChoice());

            rules.QueueFree();
        }

        /// <summary>
        /// The one place calling back in really is wrong: a host callback runs while the interpreter
        /// is mid-effect, so it must answer and return rather than start another action.
        /// </summary>
        private void AHostCallbackCannotCallBackIn()
        {
            GameplayEffectsRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            bool refused = false;

            // The callback has to answer with a value. A host that returns nothing is saying "I do
            // not know this name", and the rules carry on looking for it elsewhere.
            rules.RegisterName("meddle", Callable.From((Godot.Collections.Dictionary _) =>
            {
                try
                {
                    rules.EndTurn();
                }
                catch (InvalidOperationException)
                {
                    refused = true;
                }
                return 0;
            }));

            rules.Execute("log meddle", 0, 0);

            Check("a host callback that calls back in is refused", refused);
            Check("and the game is still sound", rules.IsInBattle() && rules.GetStat(slime, "hp") == 30);

            rules.QueueFree();
        }

        /// <summary>
        /// The conversation an attached editor has with a running game. A live session cannot be
        /// staged headlessly, so this drives the same handler the debugger capture calls, which is
        /// where the Variant shapes on both sides are decided.
        /// </summary>
        private void TheDebugChannelAnswersTheEditor()
        {
            GameplayEffectsRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            var agent = new GeDebugAgent(new GeDebugService(rules.Core));

            Check("a message for another capture is not ours",
                !agent.Respond("something_else:hello", new Godot.Collections.Array(), out _, out _));

            bool answered = agent.Respond(GeProtocol.Message(GeProtocol.Hello), new Godot.Collections.Array(), out string hello, out Godot.Collections.Dictionary who);
            Check("the game says hello", answered && hello == GeProtocol.Welcome, hello);
            Check("and says what it is running", who["definitions"].AsInt32() >= 2 && who["fingerprint"].AsString().Length == 16);
            Check("with tracing off until asked", !who["tracing"].AsBool());

            agent.Respond(GeProtocol.Message(GeProtocol.TraceEnable), new Godot.Collections.Array { true, 500 }, out _, out Godot.Collections.Dictionary traced);
            Check("the editor can turn recording on", traced["tracing"].AsBool());

            rules.Play(rules.AddCard("Ember", "hand"), slime);

            agent.Respond(GeProtocol.Message(GeProtocol.TraceFetch), new Godot.Collections.Array { 0, 50 }, out string traceReply, out Godot.Collections.Dictionary batch);
            Check("and pull what was recorded", traceReply == GeProtocol.Trace && batch["entries"].AsGodotArray().Count > 0,
                batch["entries"].AsGodotArray().Count.ToString());
            Check("each step carrying the line behind it",
                batch["entries"].AsGodotArray()[0].AsGodotDictionary().ContainsKey("file"));

            agent.Respond(GeProtocol.Message(GeProtocol.Execute), new Godot.Collections.Array { "deal 1 to enemy" }, out string ranReply, out Godot.Collections.Dictionary ran);
            Check("the console runs a statement", ranReply == GeProtocol.Ran && ran["ok"].AsBool(), ran["message"].AsString());

            agent.Respond(GeProtocol.Message(GeProtocol.Execute), new Godot.Collections.Array { "deal 1 to nonsense" }, out _, out Godot.Collections.Dictionary failed);
            Check("and reports one that cannot run", !failed["ok"].AsBool());

            var saved = new Godot.Collections.Array
            {
                "res://content/cards.ge",
                "card \"Ember\"\n  cost 1\n  target enemy\n  effect:\n    deal 9 to target\n",
            };
            agent.Respond(GeProtocol.Message(GeProtocol.Reload), saved, out string reloadReply, out Godot.Collections.Dictionary reloaded);
            Check("saving a file reaches the running game", reloadReply == GeProtocol.Reloaded && reloaded["applied"].AsBool());
            Check("and rebinds what is live", reloaded["rebound"].AsInt32() > 0, reloaded["rebound"].AsInt32().ToString());

            agent.Respond(GeProtocol.Message(GeProtocol.Entities), new Godot.Collections.Array { string.Empty, string.Empty },
                out string listReply, out Godot.Collections.Dictionary list);
            Check("the editor can ask what is in play",
                listReply == GeProtocol.EntityList && list["entities"].AsGodotArray().Count > 0);

            agent.Respond(GeProtocol.Message(GeProtocol.Entities), new Godot.Collections.Array { "hand", string.Empty },
                out _, out Godot.Collections.Dictionary inHand);
            foreach (Variant inZone in inHand["entities"].AsGodotArray())
            {
                Check("narrowed to one zone when asked", inZone.AsGodotDictionary()["zone"].AsString() == "hand");
            }

            agent.Respond(GeProtocol.Message(GeProtocol.Entity), new Godot.Collections.Array { slime },
                out string detailReply, out Godot.Collections.Dictionary detail);
            Check("and look inside one of them", detailReply == GeProtocol.EntityDetail && detail["found"].AsBool());
            Check("seeing what its stats started as", detail["base_stats"].AsGodotDictionary().ContainsKey("hp"));
            Check("the rules it carries", detail.ContainsKey("listeners") && detail.ContainsKey("modifiers"));
            Check("and whether they are live at all", detail["active"].AsBool());

            agent.Respond(GeProtocol.Message(GeProtocol.Entity), new Godot.Collections.Array { 999999 },
                out _, out Godot.Collections.Dictionary gone);
            Check("an entity that is not there is said to be missing, not faked", !gone["found"].AsBool());

            agent.Respond(GeProtocol.Message(GeProtocol.Pause), new Godot.Collections.Array(),
                out string heldReply, out Godot.Collections.Dictionary held);
            Check("the editor can hold the queue", heldReply == GeProtocol.StepState && held["paused"].AsBool());
            Check("and is told the game can be stepped at all", held["steppable"].AsBool());

            agent.Respond(GeProtocol.Message(GeProtocol.Step), new Godot.Collections.Array(),
                out _, out Godot.Collections.Dictionary stepped);
            Check("a step with nothing queued says so rather than doing nothing",
                stepped["message"].AsString().Length > 0, stepped["message"].AsString());

            agent.Respond(GeProtocol.Message(GeProtocol.BreakEvent), new Godot.Collections.Array { "damaged", true },
                out _, out Godot.Collections.Dictionary armed);
            Check("a breakpoint can be armed on an event", armed["breakpoints"].AsInt32() == 1);

            agent.Respond(GeProtocol.Message(GeProtocol.BreakLine), new Godot.Collections.Array { "res://content/cards.ge", 4, true },
                out _, out Godot.Collections.Dictionary online);
            Check("and on a line of content", online["breakpoints"].AsInt32() == 2);

            agent.Respond(GeProtocol.Message(GeProtocol.BreakClear), new Godot.Collections.Array(),
                out _, out Godot.Collections.Dictionary cleared);
            Check("and all of them taken away again", cleared["breakpoints"].AsInt32() == 0);

            agent.Respond(GeProtocol.Message(GeProtocol.Resume), new Godot.Collections.Array(),
                out _, out Godot.Collections.Dictionary running);
            Check("the game runs on when let go", !running["paused"].AsBool());

            rules.QueueFree();
        }

        // Harness ----------------------------------------------------------------------------------

        private GameplayEffectsRuntime NewRuntime()
        {
            var rules = new GameplayEffectsRuntime { AutoLoad = false, ContentFolder = ContentFolder, Seed = 7 };
            AddChild(rules);
            return rules;
        }

        private GameplayEffectsRuntime Loaded()
        {
            GameplayEffectsRuntime rules = NewRuntime();
            rules.LoadContent(ContentFolder);
            return rules;
        }

        private void OnEffectEvent(Godot.Collections.Dictionary effectEvent) => _events.Add(effectEvent["name"].AsString());

        private void OnChoiceRequested(Godot.Collections.Dictionary request) => _lastChoice = request;

        private void Check(string what, bool passed, string detail = "")
        {
            _checks++;
            if (passed) return;
            _failures.Add(what + (detail.Length == 0 ? string.Empty : " (" + detail + ")"));
        }

        private static int Errors(Godot.Collections.Array problems)
        {
            int errors = 0;
            foreach (Variant problem in problems)
            {
                if (problem.AsGodotDictionary()["severity"].AsString() == "error") errors++;
            }
            return errors;
        }

        private static string Describe(Godot.Collections.Array problems)
        {
            var lines = new List<string>();
            foreach (Variant problem in problems)
            {
                Godot.Collections.Dictionary diagnostic = problem.AsGodotDictionary();
                lines.Add($"{diagnostic["file"]}:{diagnostic["line"]} {diagnostic["code"]} {diagnostic["message"]}");
            }
            return string.Join(" | ", lines);
        }

        private static int Status(Godot.Collections.Dictionary entity, string name)
        {
            foreach (Variant status in entity["statuses"].AsGodotArray())
            {
                Godot.Collections.Dictionary view = status.AsGodotDictionary();
                if (view["name"].AsString() == name) return view["counter"].AsInt32();
            }
            return 0;
        }
    }
}
