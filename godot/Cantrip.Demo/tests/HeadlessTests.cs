using System;
using System.Collections.Generic;
using Cantrip.Diagnostics;
using Godot;

namespace Cantrip.GodotAdapter.Demo
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
                ContentLoadsAgainAfterACollection();
                ABattlePlaysOut();
                EventsArriveAfterTheAction();
                AnActionFromAnEventHandlerIsToldBeforeTheCallReturns();
                ViewsAndDescriptionsTellTheTruth();
                ADeferredChoiceRollsBackAndReplays();
                AnAnswerTurnedAwaySaysWhyInOneWord();
                SavingAndLoadingReturnsTheSameGame();
                ASaveFromBeforeAPatchThatAddsACardLoads();
                ASaveNeedingACardAPatchRemovedIsRefusedUntouched();
                APatchedWaitingBlockIsExplainedNotThrown();
                ACardIsRemovedOrUpgradedBetweenBattles();
                DefinitionsAreListedAndDescribedOutOfPlay();
                ANewRunStartsAfresh();
                ANewRunTakesUpAReloadedRuleset();
                ANewRunCutsTheOldRunsEventsShort();
                ANewRunDropsWhatThePresenterStillHadToShow();
                HotReloadChangesARunningGame();
                AChoiceIsAnsweredFromTheSignalThatAsks();
                AHostCallbackCannotCallBackIn();
                NorSaveHalfwayThroughAnEffect();
                NorSetTheGameUpFromACallback();
                ACallbackMustBeCallable();
                WonIsNullUntilABattleEnds();
                AnAutomaticLoadReportsItsProblems();
                TheDebugChannelAnswersTheEditor();

                code = _failures.Count == 0 ? 0 : 1;
            }
            catch (Exception error)
            {
                GD.PushError("Cantrip headless tests: the harness threw. " + error);
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
            CantripRuntime rules = NewRuntime();

            Godot.Collections.Array problems = rules.LoadContent(ContentFolder);
            Check("content loads with no errors", Errors(problems) == 0, Describe(problems));
            Check("content has the demo's definitions", rules.Content.Find("Ember", "card") != null && rules.Content.Find("Slime", "enemy") != null);
            Check("test blocks came with it", rules.Content.Tests.Count >= 3, rules.Content.Tests.Count + " test block(s)");

            rules.QueueFree();
        }

        /// <summary>
        /// A content file left in Godot's resource cache can be found there by the next load after
        /// the garbage collector has let go of its C# half but before Godot has freed it, and the
        /// game then crashes, or not, depending on when the collector ran. So loading leaves nothing
        /// in the cache, and content loads as often as a game or the dock asks, collections or not.
        /// </summary>
        private void ContentLoadsAgainAfterACollection()
        {
            const string cards = ContentFolder + "/cards.cantrip";

            var library = new Cantrip.Content.ContentLibrary();
            GodotContentLoader.LoadFolder(library, ContentFolder);
            Check("loading leaves no content file in Godot's resource cache", !ResourceLoader.HasCached(cards));

            for (int i = 0; i < 50; i++)
            {
                GC.Collect();
                library = new Cantrip.Content.ContentLibrary();
                GodotContentLoader.LoadFolder(library, ContentFolder);
            }
            Check("and content loads again and again with collections in between",
                library.Find("Ember", "card") != null && !library.Diagnostics.HasErrors, library.Diagnostics.ToString());
        }

        private void ABattlePlaysOut()
        {
            CantripRuntime rules = Loaded();

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
            CantripRuntime rules = Loaded();
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

        /// <summary>
        /// A handler may act again, and what that action raises is told as well, after the events
        /// that prompted it and before the first call returns, not held back for the player's next
        /// move. Whether the battle ended comes after all of them.
        /// </summary>
        private void AnActionFromAnEventHandlerIsToldBeforeTheCallReturns()
        {
            CantripRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            var told = new List<string>();
            Action<Godot.Collections.Dictionary> onEvent = effectEvent =>
            {
                told.Add(effectEvent["name"].AsString() + " " + effectEvent["amount"].AsInt32());
                if (told.Count == 1) rules.Execute("deal 99 to target", 0, slime);   // the finishing blow, from the handler
            };
            Action<bool> onEnded = won => told.Add("BattleEnded " + won);
            rules.EffectEvent += onEvent.Invoke;
            rules.BattleEnded += onEnded.Invoke;

            rules.Execute("deal 3 to target", 0, slime);

            rules.EffectEvent -= onEvent.Invoke;
            rules.BattleEnded -= onEnded.Invoke;

            Check("an action taken in an event handler is told before the first call returns",
                told.Count > 2 && told[0] == "damaged 3" && told[1] == "damaged 27", string.Join(", ", told));
            Check("and the battle it won ends once, after everything that happened",
                told[told.Count - 1] == "BattleEnded True" && told.Exists(entry => entry.StartsWith("battle_end", StringComparison.Ordinal))
                && told.FindAll(entry => entry.StartsWith("BattleEnded", StringComparison.Ordinal)).Count == 1, string.Join(", ", told));
            Check("so nothing is left for a later call to tell", rules.Buffer.Count == 0, rules.Buffer.Count.ToString());

            rules.QueueFree();
        }

        private void ViewsAndDescriptionsTellTheTruth()
        {
            CantripRuntime rules = Loaded();
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
            CantripRuntime rules = Loaded();
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

        /// <summary>
        /// A script compares <c>reason</c> against these words, so each one is pinned here as the
        /// node gives it, in snake_case like every other word it gives.
        /// </summary>
        private void AnAnswerTurnedAwaySaysWhyInOneWord()
        {
            CantripRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            void TurnedAway(string what, Godot.Collections.Dictionary answer, string word) =>
                Check(what, !answer["accepted"].AsBool() && answer["reason"].AsString() == word, answer["reason"].AsString());

            TurnedAway("with nothing asked, an answer is nothing_pending",
                rules.AnswerChoice(1, new Godot.Collections.Array()), "nothing_pending");

            int spare = rules.AddCard("Ember", "hand");
            int keep = rules.AddCard("Guard", "hand");
            int sort = rules.AddCard("Sort", "hand");
            rules.Play(sort);
            int first = rules.GetPendingChoice()["id"].AsInt32();
            rules.CancelChoice();
            rules.Play(sort);
            int request = rules.GetPendingChoice()["id"].AsInt32();

            TurnedAway("an answer to a question already dealt with is stale_request",
                rules.AnswerChoice(first, new Godot.Collections.Array { spare }), "stale_request");
            TurnedAway("a pick that was not offered is unknown_option",
                rules.AnswerChoice(request, new Godot.Collections.Array { slime }), "unknown_option");
            TurnedAway("the same pick twice is duplicate_option",
                rules.AnswerChoice(request, new Godot.Collections.Array { spare, spare }), "duplicate_option");
            TurnedAway("too few picks is too_few", rules.AnswerChoice(request, new Godot.Collections.Array()), "too_few");
            TurnedAway("too many picks is too_many",
                rules.AnswerChoice(request, new Godot.Collections.Array { spare, keep }), "too_many");

            Godot.Collections.Dictionary answered = rules.AnswerChoice(request, new Godot.Collections.Array { spare });
            Check("and an answer that is accepted is none",
                answered["accepted"].AsBool() && answered["reason"].AsString() == "none", answered["message"].AsString());

            rules.QueueFree();
        }

        private void SavingAndLoadingReturnsTheSameGame()
        {
            CantripRuntime rules = Loaded();
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

            // The right content, but no game inside: a file damaged or edited by hand.
            string untouched = rules.StateHash();
            Godot.Collections.Dictionary unreadable = LoadOrThrown(rules,
                "{\"format\":1,\"fingerprint\":\"" + rules.Content.Fingerprint + "\",\"snapshot\":\"not a game\"}");
            Check("a save whose game cannot be read is refused, not thrown",
                !unreadable["accepted"].AsBool() && unreadable["reason"].AsString() == "wrong_format", unreadable["reason"] + ": " + unreadable["message"]);
            Check("and changes nothing", rules.StateHash() == untouched);

            // The right content and a game, but in a snapshot format this Cantrip.Core does not read.
            Godot.Collections.Dictionary newer = LoadOrThrown(rules,
                "{\"format\":1,\"fingerprint\":\"" + rules.Content.Fingerprint + "\",\"snapshot\":\"{\\\"FormatVersion\\\":99}\"}");
            Check("a game saved in another snapshot format is wrong_format, not content_changed",
                !newer["accepted"].AsBool() && newer["reason"].AsString() == "wrong_format", newer["reason"] + ": " + newer["message"]);
            Check("and changes nothing either", rules.StateHash() == untouched);

            rules.QueueFree();
        }

        /// <summary>
        /// A patch that only adds a card changes the content's fingerprint, but a save made before
        /// it still has everything it needs, so it loads: the fingerprint alone refuses nothing.
        /// </summary>
        private void ASaveFromBeforeAPatchThatAddsACardLoads()
        {
            CantripRuntime before = Loaded();
            before.CreatePlayer();
            before.SpawnEnemy("Slime");
            before.StartBattle(false, false);
            int ember = before.AddCard("Ember", "hand");
            string save = before.Save();
            string saved = before.StateHash();

            CantripRuntime patched = Loaded();
            DiagnosticBag added = patched.Content.LoadText(
                "card \"Newcomer\"\n  cost 1\n  effect:\n    block 3\n", "res://tests/newcomer.cantrip");
            Check("the patch adds a card and so changes the fingerprint",
                !added.HasErrors && patched.Content.Fingerprint != before.Content.Fingerprint, added.ToString());

            Godot.Collections.Dictionary loaded = LoadOrThrown(patched, save);
            Check("a save from before a patch that only adds a card loads",
                loaded["accepted"].AsBool() && loaded["reason"].AsString() == "none", loaded["reason"] + ": " + loaded["message"]);
            Check("as the game it was", patched.StateHash() == saved, patched.StateHash() + " vs " + saved);
            Godot.Collections.Array enemies = patched.GetEnemies();
            Check("and plays on", enemies.Count == 1 && patched.Play(ember, enemies[0].AsInt32()) == "played");
            Check("with the new card there to be had", patched.PlayerId() != 0 && patched.AddCard("Newcomer", "hand") != 0);

            before.QueueFree();
            patched.QueueFree();
        }

        /// <summary>
        /// A save that names a card the patch has removed cannot be restored. The rules find that
        /// before they change anything, so the game in progress, and the choice it waits on, stay.
        /// </summary>
        private void ASaveNeedingACardAPatchRemovedIsRefusedUntouched()
        {
            CantripRuntime before = NewRuntime();
            before.LoadContent(ContentFolder);
            before.Content.LoadText("card \"Keepsake\"\n  cost 0\n  effect:\n    draw 1\n", "res://tests/keepsake.cantrip");
            before.CreatePlayer();
            before.SpawnEnemy("Slime");
            before.StartBattle(false, false);
            before.AddCard("Keepsake", "hand");
            string save = before.Save();

            CantripRuntime patched = Loaded();
            patched.CreatePlayer();
            patched.SpawnEnemy("Slime");
            patched.StartBattle(false, false);
            int spare = patched.AddCard("Ember", "hand");
            patched.Play(patched.AddCard("Sort", "hand"));
            int request = patched.GetPendingChoice()["id"].AsInt32();
            string untouched = patched.StateHash();

            Godot.Collections.Dictionary refused = LoadOrThrown(patched, save);
            Check("a save needing a card the patch removed is refused as content_changed",
                !refused["accepted"].AsBool() && refused["reason"].AsString() == "content_changed", refused["reason"] + ": " + refused["message"]);
            Check("with the rules' message, naming the card", refused["message"].AsString().Contains("\"Keepsake\""), refused["message"].AsString());
            Check("and the game is untouched", patched.StateHash() == untouched, patched.StateHash() + " vs " + untouched);
            Check("its open choice still open", patched.HasPendingChoice() && patched.GetPendingChoice()["id"].AsInt32() == request);

            Godot.Collections.Dictionary answered = patched.AnswerChoice(request, new Godot.Collections.Array { spare });
            Check("and answered as before", answered["accepted"].AsBool() && answered["result"].AsString() == "played", answered["message"].AsString());

            before.QueueFree();
            patched.QueueFree();
        }

        /// <summary>
        /// A <c>next turn:</c> block that is waiting when a patch changes it keeps the statements it
        /// was scheduled with, which the loaded content no longer has. Neither a save now nor one
        /// from before the patch can hold it, and the node has to say so in words, not throw into
        /// script, and leave the game in progress exactly as it was.
        /// </summary>
        private void APatchedWaitingBlockIsExplainedNotThrown()
        {
            const string folder = "user://headless";
            const string path = folder + "/later.cantrip";
            static string Later(int amount) => "card \"Later\"\n  cost 0\n  effect:\n    next turn:\n      block " + amount + "\n";

            CantripRuntime rules = Loaded();
            try
            {
                DirAccess.MakeDirRecursiveAbsolute(folder);
                Godot.Collections.Array added = Patch(rules, path, Later(3));
                Check("a card that waits a turn loads", Errors(added) == 0, Describe(added));

                rules.CreatePlayer();
                rules.SpawnEnemy("Slime");
                rules.StartBattle(false, false);
                rules.Play(rules.AddCard("Later", "hand"));
                Check("a block waiting from content can be saved", rules.CanSave());
                string save = rules.Save();

                Patch(rules, path, Later(9));
                Check("once a reload has changed the waiting block, a save cannot hold it", !rules.CanSave());
                string why = SaveError(rules);
                Check("and Save says so, not that effects are resolving", why.Contains("reload") && !why.Contains("resolving"), why);

                int spare = rules.AddCard("Ember", "hand");
                rules.Play(rules.AddCard("Sort", "hand"));
                int request = rules.GetPendingChoice()["id"].AsInt32();
                string before = rules.StateHash();

                Godot.Collections.Dictionary loaded = LoadOrThrown(rules, save);
                Check("a save from before the patch is refused, not thrown", !loaded["accepted"].AsBool(), loaded["reason"].AsString());
                Check("as content_changed, with the rules' reason",
                    loaded["reason"].AsString() == "content_changed" && loaded["message"].AsString().Contains("Later"),
                    loaded["reason"] + ": " + loaded["message"]);
                Check("and the game is as it was", rules.StateHash() == before, rules.StateHash() + " vs " + before);
                Check("its open choice still open", rules.HasPendingChoice() && rules.GetPendingChoice()["id"].AsInt32() == request);

                Godot.Collections.Dictionary answered = rules.AnswerChoice(request, new Godot.Collections.Array { spare });
                Check("and answered as before", answered["accepted"].AsBool() && answered["result"].AsString() == "played", answered["message"].AsString());

                rules.EndTurn();
                Check("once the block has run, saving works again", rules.CanSave());
            }
            finally
            {
                DirAccess.RemoveAbsolute(path);
                DirAccess.RemoveAbsolute(folder);
                rules.QueueFree();
            }
        }

        /// <summary>
        /// A deckbuilder between battles: the deck is the draw pile, a card leaves it for good, and
        /// an upgrade is the card's upgraded definition put in its place.
        /// </summary>
        private void ACardIsRemovedOrUpgradedBetweenBattles()
        {
            CantripRuntime rules = Loaded();
            DiagnosticBag upgrades = rules.Content.LoadText(
                "card \"Guard+\"\n  cost 1\n  tags skill, upgraded\n  effect:\n    block 9\n", "res://tests/upgrades.cantrip");
            Check("an upgraded definition loads", !upgrades.HasErrors, upgrades.ToString());

            _events.Clear();
            rules.EffectEvent += OnEffectEvent;

            int player = rules.CreatePlayer();
            Godot.Collections.Array deck = rules.AddDeck(new Godot.Collections.Array { "Ember", "Guard", "Sort" });
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, true);
            rules.Execute("deal 99 to target", 0, slime);
            Check("the battle is won, and every card is back in the draw pile",
                rules.GetWon().AsBool() && rules.GetZone(0, "draw").Count == 3, Names(rules, rules.GetZone(0, "draw")));

            int sort = deck[2].AsInt32();
            _events.Clear();
            Check("RemoveCard takes a card out of the deck", rules.RemoveCard(sort) && !rules.GetZone(0, "draw").Contains(sort));
            Check("content hears it as destroyed", _events.Contains("destroyed"), string.Join(", ", _events));
            Check("and it is gone for good", rules.GetEntity(sort)["removed"].AsBool());
            Check("a card already gone is not removed again", !rules.RemoveCard(sort));
            Check("nor is anything that is not a card",
                !rules.RemoveCard(slime) && !rules.RemoveCard(player) && !rules.RemoveCard(0) && !rules.RemoveCard(999999));
            Check("so the player is still there", rules.PlayerId() == player && rules.GetStat(player, "hp") > 0);

            int guard = deck[1].AsInt32();
            string upgraded = rules.GetEntity(guard)["name"].AsString() + "+";
            Check("a card's upgraded definition is found by its name", rules.DescribeDefinition(upgraded, "card").Count > 0, upgraded);
            rules.RemoveCard(guard);
            int better = rules.AddCard(upgraded, "draw");
            Check("an upgrade is the upgraded definition put in its place",
                Names(rules, rules.GetZone(0, "draw")) == "Ember, Guard+", Names(rules, rules.GetZone(0, "draw")));
            Check("and a card with no upgraded definition has none to find", rules.DescribeDefinition("Ember+", "card").Count == 0);

            int ember = deck[0].AsInt32();

            rules.SpawnEnemy("Slime");
            rules.StartBattle(false, true);
            Check("and the next battle is dealt from the deck as it now is",
                rules.GetHand().Count == 2 && rules.GetHand().Contains(better) && rules.GetHand().Contains(ember), Names(rules, rules.GetHand()));

            rules.EffectEvent -= OnEffectEvent;
            rules.QueueFree();
        }

        /// <summary>
        /// A reward screen or a shop shows cards that are not in play yet: listed by kind and tag,
        /// and described with their printed values, without the rules having to exist.
        /// </summary>
        private void DefinitionsAreListedAndDescribedOutOfPlay()
        {
            CantripRuntime rules = NewRuntime();
            rules.LoadContent(ContentFolder);

            Check("every card is listed by name, sorted", Joined(rules.GetDefinitions("card", "")) == "Ember, Guard, Sort",
                Joined(rules.GetDefinitions("card", "")));
            Check("a tag narrows the list", Joined(rules.GetDefinitions("card", "skill")) == "Guard, Sort", Joined(rules.GetDefinitions("card", "skill")));
            Check("any kind can be listed", Joined(rules.GetDefinitions("enemy", "")) == "Slime" && Joined(rules.GetDefinitions("status", "fire")) == "Burn");
            Check("a kind or a tag nothing has lists nothing",
                rules.GetDefinitions("card", "nothing").Count == 0 && rules.GetDefinitions("potion", "").Count == 0);

            Godot.Collections.Dictionary guard = rules.DescribeDefinition("Guard", "card");
            Check("a definition describes itself out of play", guard["plain"].AsString() == "Gain 6 Block.", guard["plain"].AsString());
            Check("with its cost", guard.ContainsKey("cost") && guard["cost"].AsGodotDictionary()["current"].AsInt32() == 1);
            Check("an empty kind finds it by name alone", rules.DescribeDefinition("Guard", "")["plain"].AsString() == "Gain 6 Block.");
            Godot.Collections.Dictionary anyCase = rules.DescribeDefinition("guard", "Card");
            Check("and a kind is matched without regard to case, in both",
                anyCase.ContainsKey("plain") && anyCase["plain"].AsString() == "Gain 6 Block." && Joined(rules.GetDefinitions("Card", "")) == "Ember, Guard, Sort",
                anyCase.Count + " key(s); " + Joined(rules.GetDefinitions("Card", "")));
            Check("and a name nothing has, or not of that kind, is empty",
                rules.DescribeDefinition("Nothing", "").Count == 0 && rules.DescribeDefinition("Guard", "enemy").Count == 0);

            Check("neither brings the rules into being, so content can still be loaded",
                !Throws<InvalidOperationException>(() => rules.LoadContent(ContentFolder)));

            rules.CreatePlayer();
            rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);
            int ember = rules.AddCard("Ember", "hand");
            Godot.Collections.Dictionary printed = rules.DescribeDefinition("Ember", "card");
            Godot.Collections.Dictionary live = rules.Describe(ember, 0);
            Check("with nothing changing a card, its text is the one Describe gives in play",
                printed["plain"].AsString() == live["plain"].AsString() && printed["bbcode"].AsString() == live["bbcode"].AsString(),
                printed["plain"] + " vs " + live["plain"]);
            Check("in the same shape", new HashSet<string>(Keys(printed)).SetEquals(Keys(live)), string.Join(", ", Keys(printed)));

            rules.QueueFree();
        }

        /// <summary>
        /// A new run on the same node: the rules begin again from the loaded content, reading the
        /// seed afresh, and nothing of the old run is left to answer or to hear.
        /// </summary>
        private void ANewRunStartsAfresh()
        {
            CantripRuntime rules = Loaded();
            rules.RegisterName("three", Callable.From((Godot.Collections.Dictionary _) => 3));
            _lastChoice = new Godot.Collections.Dictionary();
            rules.ChoiceRequested += OnChoiceRequested;

            rules.CreatePlayer();
            rules.AddDeck(new Godot.Collections.Array { "Ember", "Guard" });
            rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);
            int keep = rules.AddCard("Guard", "hand");
            rules.Play(rules.AddCard("Sort", "hand"));
            int request = _lastChoice.Count > 0 ? _lastChoice["id"].AsInt32() : 0;
            Check("the old run was waiting on a choice", request > 0 && rules.HasPendingChoice());
            CardRuntime old = rules.Core;

            rules.Seed = 9;
            rules.NewRun();
            rules.ChoiceRequested -= OnChoiceRequested;

            Check("a new run has no player, no battle and no enemies",
                rules.PlayerId() == 0 && !rules.IsInBattle() && rules.GetEnemies().Count == 0 && rules.GetWon().VariantType == Variant.Type.Nil);
            Check("nothing is waiting to be answered", !rules.HasPendingChoice()
                && rules.AnswerChoice(request, new Godot.Collections.Array { keep })["reason"].AsString() == "nothing_pending");
            Check("its Core is a new object", !ReferenceEquals(old, rules.Core));
            Check("content is not loaded again over it", Throws<InvalidOperationException>(() => rules.LoadContent(ContentFolder)));

            var fresh = new CantripRuntime { AutoLoad = false, ContentFolder = ContentFolder, Seed = 9 };
            AddChild(fresh);
            fresh.LoadContent(ContentFolder);
            CantripRuntime seven = Loaded();

            foreach (CantripRuntime run in new[] { rules, fresh, seven })
            {
                Check("and it plays from the start", run.CreatePlayer() != 0);
                run.AddDeck(new Godot.Collections.Array { "Ember", "Guard", "Sort", "Guard", "Ember", "Sort" });
                run.SpawnEnemy("Slime");
                run.StartBattle(true, true);
            }
            Check("exactly as a fresh node with the new seed does", rules.StateHash() == fresh.StateHash(), rules.StateHash() + " vs " + fresh.StateHash());
            Check("which is not how the old seed plays", rules.StateHash() != seven.StateHash());

            int slime = rules.GetEnemies()[0].AsInt32();
            rules.Execute("deal three to target", 0, slime);
            Check("the callables registered stay registered", rules.GetStat(slime, "hp") == 27, rules.GetStat(slime, "hp").ToString());

            fresh.QueueFree();
            seven.QueueFree();
            rules.QueueFree();
        }

        /// <summary>
        /// A running game keeps the ruleset it started with, whatever a reload says; a new run is
        /// the point at which a changed one takes effect.
        /// </summary>
        private void ANewRunTakesUpAReloadedRuleset()
        {
            const string folder = "user://headless";
            const string path = folder + "/ruleset.cantrip";
            var deck = new Godot.Collections.Array { "Ember", "Guard", "Sort", "Ember", "Guard", "Sort" };

            CantripRuntime rules = Loaded();
            try
            {
                DirAccess.MakeDirRecursiveAbsolute(folder);
                rules.CreatePlayer();
                rules.AddDeck(deck);
                rules.SpawnEnemy("Slime");
                rules.StartBattle(false, true);
                Check("the default ruleset deals five cards", rules.GetHand().Count == 5, rules.GetHand().Count.ToString());

                using (FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write)) file.StoreString("ruleset\n  hand_size 2\n");
                Godot.Collections.Dictionary report = rules.ReloadContent(new Godot.Collections.Array { path });
                Check("a reload that changes the ruleset says so", report["ruleset_changed"].AsBool());

                rules.NewRun();
                rules.CreatePlayer();
                rules.AddDeck(deck);
                rules.SpawnEnemy("Slime");
                rules.StartBattle(false, true);
                Check("and a new run plays by it", rules.GetHand().Count == 2, rules.GetHand().Count.ToString());
            }
            finally
            {
                DirAccess.RemoveAbsolute(path);
                DirAccess.RemoveAbsolute(folder);
                rules.QueueFree();
            }
        }

        /// <summary>
        /// A handler may start a new run while the old run's events are still being told. The rest
        /// of them name entities that are gone, and ids the new run will reuse, so they stop there.
        /// </summary>
        private void ANewRunCutsTheOldRunsEventsShort()
        {
            CantripRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            var heard = new List<string>();
            bool ended = false;
            Action<Godot.Collections.Dictionary> onEvent = effectEvent =>
            {
                heard.Add(effectEvent["name"].AsString());
                if (heard.Count == 1) rules.NewRun();
            };
            Action<bool> onEnded = won => ended = true;
            rules.EffectEvent += onEvent.Invoke;
            rules.BattleEnded += onEnded.Invoke;

            rules.Execute("deal 99 to target", 0, slime);

            rules.EffectEvent -= onEvent.Invoke;
            rules.BattleEnded -= onEnded.Invoke;

            Check("a new run started from an event handler hears nothing more of the old one", heard.Count == 1, string.Join(", ", heard));
            Check("not even that its battle ended", !ended);
            Check("and is ready to play", rules.PlayerId() == 0 && rules.CreatePlayer() != 0);

            rules.QueueFree();
        }

        /// <summary>What a presenter still had to show belongs to the old run, so a new run drops it.</summary>
        private void ANewRunDropsWhatThePresenterStillHadToShow()
        {
            var presenter = new BattlePresenter();
            AddChild(presenter);
            var rules = new CantripRuntime { AutoLoad = false, ContentFolder = ContentFolder, Seed = 7, Presenter = presenter };
            AddChild(rules);
            rules.LoadContent(ContentFolder);

            int presented = 0;
            bool settled = false;
            Action<Godot.Collections.Dictionary> onPresent = _ => presented++;   // and never Done: an animation still playing
            Action onSettled = () => settled = true;
            presenter.Present += onPresent.Invoke;
            presenter.Settled += onSettled.Invoke;

            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);
            rules.Play(rules.AddCard("Ember", "hand"), slime);
            Check("the presenter is part way through the old run's events", presenter.IsBusy() && presenter.PendingCount() > 0 && presented == 1);

            rules.NewRun();
            Check("a new run drops the rest", presenter.IsSettled() && settled && presented == 1);

            presenter.Present -= onPresent.Invoke;
            presenter.Settled -= onSettled.Invoke;
            rules.QueueFree();
            presenter.QueueFree();
        }

        private void HotReloadChangesARunningGame()
        {
            CantripRuntime rules = Loaded();
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
            CantripRuntime rules = Loaded();
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
            CantripRuntime rules = Loaded();
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
        /// A save taken in the middle of a card's effect would hold half an action. The rules see
        /// that effects are resolving only while their queue runs, which a card's own effect does
        /// not, so it is the node that has to refuse, as <c>CanSave</c> already says it will.
        /// </summary>
        private void NorSaveHalfwayThroughAnEffect()
        {
            CantripRuntime rules = Loaded();
            DiagnosticBag peek = rules.Content.LoadText(
                "card \"Peek\"\n  cost 0\n  target enemy\n  effect:\n    deal peek to target\n    block 5\n", "res://tests/peek.cantrip");
            Check("a card whose effect asks a callback loads", !peek.HasErrors, peek.ToString());

            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);
            int card = rules.AddCard("Peek", "hand");

            bool couldSave = true;
            string why = "not asked";
            rules.RegisterName("peek", Callable.From((Godot.Collections.Dictionary _) =>
            {
                couldSave = rules.CanSave();
                why = SaveError(rules);
                return 1;
            }));

            Check("the card plays", rules.Play(card, slime) == "played");
            Check("in the middle of its effect, CanSave is false", !couldSave);
            Check("and Save refuses, saying why", why.Contains("resolving"), why);
            Check("once the card has resolved, saving works again", rules.CanSave() && SaveError(rules) == "saved");

            rules.QueueFree();
        }

        /// <summary>
        /// Setting a game up runs no rules, but it changes the game all the same, so a callback in
        /// the middle of an effect may no more add a card or load a save than play one. Nor may a
        /// callback that a query called: working out a cost evaluates content too.
        /// </summary>
        private void NorSetTheGameUpFromACallback()
        {
            CantripRuntime rules = Loaded();
            DiagnosticBag priced = rules.Content.LoadText("card \"Priced\"\n  cost 1\n  modify cost: +meddle\n", "res://tests/priced.cantrip");
            Check("a card whose cost asks a callback loads", !priced.HasErrors, priced.ToString());

            int player = rules.CreatePlayer();
            rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);
            int card = rules.AddCard("Priced", "hand");
            string save = rules.Save();

            var refused = new List<string>();
            var allowed = new List<string>();
            void Attempt(string call, Action action)
            {
                try
                {
                    action();
                    allowed.Add(call);
                }
                catch (InvalidOperationException error) when (error.Message.StartsWith("The rules are resolving", StringComparison.Ordinal))
                {
                    refused.Add(call);
                }
                catch (Exception error)
                {
                    allowed.Add(call + " (failed for another reason: " + error.Message + ")");
                }
            }

            rules.RegisterName("meddle", Callable.From((Godot.Collections.Dictionary _) =>
            {
                Attempt("CreatePlayer", () => rules.CreatePlayer());
                Attempt("AddCard", () => rules.AddCard("Ember", "hand"));
                Attempt("AddDeck", () => rules.AddDeck(new Godot.Collections.Array { "Guard" }));
                Attempt("SpawnEnemy", () => rules.SpawnEnemy("Slime"));
                Attempt("GrantAbility", () => rules.GrantAbility("Anything", player));
                Attempt("LoadSave", () => rules.LoadSave(save));
                Attempt("CancelChoice", () => rules.CancelChoice());
                Attempt("RemoveCard", () => rules.RemoveCard(card));
                Attempt("NewRun", () => rules.NewRun());
                return 0;
            }));

            rules.Execute("log meddle", 0, 0);
            Check("every setup call is refused from a callback an effect made",
                allowed.Count == 0 && new HashSet<string>(refused).Count == 9, "allowed: " + string.Join(", ", allowed));

            refused.Clear();
            int cost = rules.CostOf(card);
            Check("and from one a query made",
                allowed.Count == 0 && new HashSet<string>(refused).Count == 9 && cost == 1, $"cost {cost}, allowed: " + string.Join(", ", allowed));

            Check("so the game was not set up behind the rules' back",
                rules.GetHand().Count == 1 && rules.GetZone(0, "draw").Count == 0 && rules.GetEnemies().Count == 1);

            rules.QueueFree();
        }

        /// <summary>
        /// A callback is kept as the Variant it arrived in, so the boundary accepts anything. What
        /// could never be called is refused on registration, not the first time content asks.
        /// </summary>
        private void ACallbackMustBeCallable()
        {
            CantripRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            Check("a number is not a callback", Throws<ArgumentException>(() => rules.RegisterName("seven", 7)));
            Check("nor is a method that does not exist",
                Throws<ArgumentException>(() => rules.RegisterFunction("nothing", new Callable(this, "NoSuchMethod"))));

            rules.RegisterName("four", Callable.From((Godot.Collections.Dictionary _) => 4));
            rules.Execute("deal four to target", 0, slime);
            Check("a C# delegate still answers", rules.GetStat(slime, "hp") == 26, rules.GetStat(slime, "hp").ToString());

            rules.QueueFree();
        }

        /// <summary>
        /// Three answers, and script can only tell "not yet" from "lost" if the first is a real
        /// null. In a conditional against a bool, a bare default is false.
        /// </summary>
        private void WonIsNullUntilABattleEnds()
        {
            CantripRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            Check("before any battle, nothing is won or lost", rules.GetWon().VariantType == Variant.Type.Nil, rules.GetWon().ToString());

            rules.StartBattle(false, false);
            Check("nor while one runs", rules.GetWon().VariantType == Variant.Type.Nil, rules.GetWon().ToString());

            rules.Execute("deal 99 to target", 0, slime);
            Check("a battle won reads as true",
                !rules.IsInBattle() && rules.GetWon().VariantType == Variant.Type.Bool && rules.GetWon().AsBool(), rules.GetWon().ToString());

            rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);
            Check("and as null again once the next battle starts", rules.GetWon().VariantType == Variant.Type.Nil, rules.GetWon().ToString());

            rules.QueueFree();
        }

        /// <summary>
        /// With AutoLoad on, nothing receives what the load returns, so its problems have to reach
        /// the Output panel by themselves or a running game never mentions them.
        /// </summary>
        private void AnAutomaticLoadReportsItsProblems()
        {
            GD.Print("HEADLESS: loading the deliberately broken fixture; the errors it reports next are expected.");

            var heard = new ErrorLog();
            OS.AddLogger(heard);
            var rules = new CantripRuntime { ContentFolder = "res://tests/fixtures", Seed = 7 };
            try
            {
                AddChild(rules);   // AutoLoad is on, so this loads
            }
            finally
            {
                OS.RemoveLogger(heard);
            }

            string expected = "res://tests/fixtures/broken.cantrip:6:18: error CT0018: ";
            Check("an automatic load reports each error on one line, as the importer words it",
                heard.Errors.Exists(line => line.StartsWith(expected, StringComparison.Ordinal)), string.Join(" | ", heard.Errors));

            rules.QueueFree();
        }

        /// <summary>
        /// The conversation an attached editor has with a running game. A live session cannot be
        /// staged headlessly, so this drives the same handler the debugger capture calls, which is
        /// where the Variant shapes on both sides are decided.
        /// </summary>
        private void TheDebugChannelAnswersTheEditor()
        {
            CantripRuntime rules = Loaded();
            rules.CreatePlayer();
            int slime = rules.SpawnEnemy("Slime");
            rules.StartBattle(false, false);

            var agent = new CantripDebugAgent(new CantripDebugService(rules.Core));

            Check("a message for another capture is not ours",
                !agent.Respond("something_else:hello", new Godot.Collections.Array(), out _, out _));

            bool answered = agent.Respond(CantripProtocol.Message(CantripProtocol.Hello), new Godot.Collections.Array(), out string hello, out Godot.Collections.Dictionary who);
            Check("the game says hello", answered && hello == CantripProtocol.Welcome, hello);
            Check("and says what it is running", who["definitions"].AsInt32() >= 2 && who["fingerprint"].AsString().Length == 16);
            Check("with tracing off until asked", !who["tracing"].AsBool());

            agent.Respond(CantripProtocol.Message(CantripProtocol.TraceEnable), new Godot.Collections.Array { true, 500 }, out _, out Godot.Collections.Dictionary traced);
            Check("the editor can turn recording on", traced["tracing"].AsBool());

            rules.Play(rules.AddCard("Ember", "hand"), slime);

            agent.Respond(CantripProtocol.Message(CantripProtocol.TraceFetch), new Godot.Collections.Array { 0, 50 }, out string traceReply, out Godot.Collections.Dictionary batch);
            Check("and pull what was recorded", traceReply == CantripProtocol.Trace && batch["entries"].AsGodotArray().Count > 0,
                batch["entries"].AsGodotArray().Count.ToString());
            Check("each step carrying the line behind it",
                batch["entries"].AsGodotArray()[0].AsGodotDictionary().ContainsKey("file"));

            agent.Respond(CantripProtocol.Message(CantripProtocol.Execute), new Godot.Collections.Array { "deal 1 to enemy" }, out string ranReply, out Godot.Collections.Dictionary ran);
            Check("the console runs a statement", ranReply == CantripProtocol.Ran && ran["ok"].AsBool(), ran["message"].AsString());

            agent.Respond(CantripProtocol.Message(CantripProtocol.Execute), new Godot.Collections.Array { "deal 1 to nonsense" }, out _, out Godot.Collections.Dictionary failed);
            Check("and reports one that cannot run", !failed["ok"].AsBool());

            var saved = new Godot.Collections.Array
            {
                "res://content/cards.cantrip",
                "card \"Ember\"\n  cost 1\n  target enemy\n  effect:\n    deal 9 to target\n",
            };
            agent.Respond(CantripProtocol.Message(CantripProtocol.Reload), saved, out string reloadReply, out Godot.Collections.Dictionary reloaded);
            Check("saving a file reaches the running game", reloadReply == CantripProtocol.Reloaded && reloaded["applied"].AsBool());
            Check("and rebinds what is live", reloaded["rebound"].AsInt32() > 0, reloaded["rebound"].AsInt32().ToString());

            agent.Respond(CantripProtocol.Message(CantripProtocol.Entities), new Godot.Collections.Array { string.Empty, string.Empty },
                out string listReply, out Godot.Collections.Dictionary list);
            Check("the editor can ask what is in play",
                listReply == CantripProtocol.EntityList && list["entities"].AsGodotArray().Count > 0);

            agent.Respond(CantripProtocol.Message(CantripProtocol.Entities), new Godot.Collections.Array { "hand", string.Empty },
                out _, out Godot.Collections.Dictionary inHand);
            foreach (Variant inZone in inHand["entities"].AsGodotArray())
            {
                Check("narrowed to one zone when asked", inZone.AsGodotDictionary()["zone"].AsString() == "hand");
            }

            agent.Respond(CantripProtocol.Message(CantripProtocol.Entity), new Godot.Collections.Array { slime },
                out string detailReply, out Godot.Collections.Dictionary detail);
            Check("and look inside one of them", detailReply == CantripProtocol.EntityDetail && detail["found"].AsBool());
            Check("seeing what its stats started as", detail["base_stats"].AsGodotDictionary().ContainsKey("hp"));
            Check("the rules it carries", detail.ContainsKey("listeners") && detail.ContainsKey("modifiers"));
            Check("and whether they are live at all", detail["active"].AsBool());

            agent.Respond(CantripProtocol.Message(CantripProtocol.Entity), new Godot.Collections.Array { 999999 },
                out _, out Godot.Collections.Dictionary gone);
            Check("an entity that is not there is said to be missing, not faked", !gone["found"].AsBool());

            agent.Respond(CantripProtocol.Message(CantripProtocol.Pause), new Godot.Collections.Array(),
                out string heldReply, out Godot.Collections.Dictionary held);
            Check("the editor can hold the queue", heldReply == CantripProtocol.StepState && held["paused"].AsBool());
            Check("and is told the game can be stepped at all", held["steppable"].AsBool());

            agent.Respond(CantripProtocol.Message(CantripProtocol.Step), new Godot.Collections.Array(),
                out _, out Godot.Collections.Dictionary stepped);
            Check("a step with nothing queued says so rather than doing nothing",
                stepped["message"].AsString().Length > 0, stepped["message"].AsString());

            agent.Respond(CantripProtocol.Message(CantripProtocol.BreakEvent), new Godot.Collections.Array { "damaged", true },
                out _, out Godot.Collections.Dictionary armed);
            Check("a breakpoint can be armed on an event", armed["breakpoints"].AsInt32() == 1);

            agent.Respond(CantripProtocol.Message(CantripProtocol.BreakLine), new Godot.Collections.Array { "res://content/cards.cantrip", 4, true },
                out _, out Godot.Collections.Dictionary online);
            Check("and on a line of content", online["breakpoints"].AsInt32() == 2);

            agent.Respond(CantripProtocol.Message(CantripProtocol.BreakClear), new Godot.Collections.Array(),
                out _, out Godot.Collections.Dictionary cleared);
            Check("and all of them taken away again", cleared["breakpoints"].AsInt32() == 0);

            agent.Respond(CantripProtocol.Message(CantripProtocol.Resume), new Godot.Collections.Array(),
                out _, out Godot.Collections.Dictionary running);
            Check("the game runs on when let go", !running["paused"].AsBool());

            rules.QueueFree();
        }

        // Harness ----------------------------------------------------------------------------------

        private CantripRuntime NewRuntime()
        {
            var rules = new CantripRuntime { AutoLoad = false, ContentFolder = ContentFolder, Seed = 7 };
            AddChild(rules);
            return rules;
        }

        private CantripRuntime Loaded()
        {
            CantripRuntime rules = NewRuntime();
            rules.LoadContent(ContentFolder);
            return rules;
        }

        /// <summary>Writes a content file and reloads it into the running game, as a patch arrives.</summary>
        private static Godot.Collections.Array Patch(CantripRuntime rules, string path, string text)
        {
            using (FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write)) file.StoreString(text);
            return rules.ReloadContent(new Godot.Collections.Array { path })["diagnostics"].AsGodotArray();
        }

        /// <summary>What Save says when it cannot save, or "saved".</summary>
        private static string SaveError(CantripRuntime rules)
        {
            try
            {
                rules.Save();
                return "saved";
            }
            catch (InvalidOperationException error)
            {
                return error.Message;
            }
        }

        /// <summary>
        /// LoadSave, with anything it throws turned into a refusal, so a load that throws fails its
        /// checks instead of ending the run.
        /// </summary>
        private static Godot.Collections.Dictionary LoadOrThrown(CantripRuntime rules, string save)
        {
            try
            {
                return rules.LoadSave(save);
            }
            catch (Exception error)
            {
                return new Godot.Collections.Dictionary
                {
                    ["accepted"] = false,
                    ["reason"] = "threw " + error.GetType().Name,
                    ["message"] = error.Message,
                };
            }
        }

        private void OnEffectEvent(Godot.Collections.Dictionary effectEvent) => _events.Add(effectEvent["name"].AsString());

        private void OnChoiceRequested(Godot.Collections.Dictionary request) => _lastChoice = request;

        private void Check(string what, bool passed, string detail = "")
        {
            _checks++;
            if (passed) return;
            _failures.Add(what + (detail.Length == 0 ? string.Empty : " (" + detail + ")"));
        }

        private static bool Throws<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (TException)
            {
                return true;
            }
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

        /// <summary>The names of these entities, sorted, as one line.</summary>
        private static string Names(CantripRuntime rules, Godot.Collections.Array ids)
        {
            var names = new List<string>();
            foreach (Variant id in ids) names.Add(rules.GetEntity(id.AsInt32())["name"].AsString());
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names);
        }

        /// <summary>An array of strings as one line, in its own order.</summary>
        private static string Joined(Godot.Collections.Array values)
        {
            var parts = new List<string>();
            foreach (Variant value in values) parts.Add(value.AsString());
            return string.Join(", ", parts);
        }

        private static List<string> Keys(Godot.Collections.Dictionary dictionary)
        {
            var keys = new List<string>();
            foreach (Variant key in dictionary.Keys) keys.Add(key.AsString());
            return keys;
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

        /// <summary>Hears what reaches the Output panel as an error, as a person watching it would.</summary>
        private sealed partial class ErrorLog : Logger
        {
            public List<string> Errors { get; } = new List<string>();

            public override void _LogError(string function, string file, int line, string code, string rationale,
                bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
            {
                // push_error puts its text in code; an engine error keeps the condition there and
                // explains it in rationale.
                if (errorType != (int)ErrorType.Error) return;
                lock (Errors) Errors.Add(string.IsNullOrEmpty(rationale) ? code : rationale);
            }
        }
    }
}
