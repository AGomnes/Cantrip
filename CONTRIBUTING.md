# Contributing

Thanks for looking. Cantrip is maintained by one person, so the most useful contributions are the ones that are easy to act on.

## Issues are the best way in

The [issue forms](https://github.com/AGomnes/Cantrip/issues/new/choose) ask for what each kind of report needs:

- **Bugs**: where it happened, the smallest thing that shows the problem, what you expected, and what happened, plus the output of `dotnet cantrip --version` (in a Godot project without the tool, the version in `addons/cantrip/plugin.cfg` and the Cantrip.Core version in your `.csproj`). A `.cantrip` file with a failing `test` block is ideal, because it describes the problem and proves the fix at once. Where the bug is not in the content, send the smallest C# or GDScript instead, or the command you ran and its output.
- **Effects the language cannot express**: name the game and the card, relic or ability, and say what it does. That is how [docs/coverage.md](docs/coverage.md) grows, and it is the most valuable thing an outside user can tell this project.
- **Rough edges** (the *First-run report* form): anything that confused you in the [quickstart](docs/quickstart.md) or the docs. [Troubleshooting](docs/troubleshooting.md) may already answer it; report it anyway, and say whether you found it there. If you had to guess, the docs are wrong.

## The repository

Godot is Cantrip's primary engine, so the addon comes first here even though the library under it is engine-free.

| Path | Contents |
|---|---|
| `godot/Cantrip.Demo/addons/cantrip` | The Godot addon itself: the `CantripRuntime` node, the `.cantrip` importer, the editor dock and the debugger tabs. This copy is the one each release packages and publishes, and the demo around it uses it in place |
| `godot/Cantrip.Demo` | The Godot project holding it: a demo battle driven from GDScript, and the headless scenes CI runs. [Working on the addon](docs/godot.md#working-on-the-addon) says how to build, run and package it |
| `src/Cantrip.Core` | The library: parser, content loading, rules engine, interpreter, linter, rules text, test runner |
| `src/Cantrip.Cli` | The `cantrip` tool |
| `src/Cantrip.Sim` | What `cantrip sim` runs, as a library shipped inside the tool: the scenario runner, its bots, and the meter that records what the engine raised |
| `samples` | Worked content with its tests, one folder to a game. [samples/README.md](samples/README.md) says what each folder shows, and how to test, lint and simulate it |
| `samples/basic` | Small examples of most features, including the Fireball, Frozen and Kindling in the README, and the Strike, Defend and Jaw Worm its C# example uses |
| `samples/corpus` | Effects re-created from existing games |
| `samples/abilities` | A fight with no cards in it, as a turn-based roguelike has: abilities on cooldowns, a bleed that ticks, an enemy whose moves change when it is wounded |
| `samples/party` | A party of heroes the game asks for input in turn, each with its own abilities, and an enemy that telegraphs which of them it will hit |
| `samples/board` | A fight on a board with more than one row, as a Monster Train style train has: three floors, three slots to a floor, and a floor with no room that turns a summon away |
| `samples/slice` | A small roguelite: a witch climbing a five-floor tower, fire against frost, with a boss that changes its moves at half health |
| `samples/recipes` | The recipes from [docs/writing-content.md](docs/writing-content.md), with their tests |
| `reference` | The Drowned Chapel: a whole turn-based roguelite, built with the library from the published docs. A party of three on a board under `turns: initiative`, cards and abilities together, a C# host that plays a descent headlessly, and a Godot front end of its own. [reference/README.md](reference/README.md) says how to run it; [reference/FINDINGS.md](reference/FINDINGS.md) says what hurt while building it |
| `realtime` | Emberline: the same again, on the tick clock. No turns anywhere, cooldowns in seconds, enemies acting from their own `on every` listeners, a host that owns the frame loop, and a Godot front end of its own. [realtime/README.md](realtime/README.md) and [realtime/FINDINGS.md](realtime/FINDINGS.md) say the same for it. Neither game's host is in `Cantrip.sln`, but CI lints, tests and plays both headlessly on every push, in a console host and inside Godot, so a change has to keep two whole games working |
| `tests` | Unit tests for the library and for the addon's engine-free layer |
| `tools` | Packaging the addon, checking the quickstart and the addon install against freshly built packages, generating `docs/api` from the sources (`tools/api-docs.sh`, `tools/Cantrip.ApiDoc`), and publishing the reference game trimmed and AOT and playing it (`tools/publish-check.sh`) |
| `docs/api` | The API reference. Generated: never edit it by hand, and CI fails when it disagrees with the sources |

## Pull requests

Please open an issue first for anything beyond a small fix, so we can agree on the approach before you spend time on it. Then:

- `dotnet build Cantrip.sln` has no warnings, and `dotnet test tests/Cantrip.Core.Tests` and `dotnet test tests/Cantrip.Godot.Tests` pass.
- Lint passes with no warnings, and the tests pass, on each content folder, each loaded on its own, with the tool run from source. CI lints with `--warnings-as-errors`, so a warning fails the build there. There are nine folders: the seven in `samples/`, and the content of the two whole games, `reference/content` and `realtime/content`.
  ```
  dotnet run --project src/Cantrip.Cli -- lint samples/basic --warnings-as-errors
  dotnet run --project src/Cantrip.Cli -- test samples/basic
  ```
  Put `samples/corpus`, `samples/recipes`, `samples/abilities`, `samples/board`, `samples/party`, `samples/slice`, `reference/content` or `realtime/content` in place of `samples/basic` for the rest.
- `sim` passes on the five folders that hold a `scenario`: `samples/slice`, `samples/abilities`, `samples/board`, `samples/party` and `reference/content`. CI plays 100 runs of each, and both default bots play every run, which is where a run that throws, a battle that never ends, a card that was never playable or an enemy move that never fired shows up:
  ```
  dotnet run --project src/Cantrip.Cli -- sim samples/slice --runs 100
  ```
  `realtime/content` has no scenario and cannot have one: a bot plays a scenario by taking turns and that game has none, so `sim` refuses the folder, and a `scenario` written in `clock ticks` content is error CT338 at lint.
- Both games still play themselves. Each plays whole runs headlessly, takes a save part-way through, restores it into a fresh runtime and plays on from both, which then have to stay the same game, hash for hash. That is the cover for saving, and for a change that only shows up when several features are used at once rather than one at a time:
  ```
  dotnet run --project reference/host -c Release -- --check
  dotnet run --project realtime/host -c Release -- --check
  ```
- A change to the addon builds the demo and passes the headless Godot scenes, the dock's self-test and the install test in a blank project, which are the commands in [Working on the addon](docs/godot.md#working-on-the-addon). CI runs those, and then assembles both games' Godot projects against the same addon and plays them, so an addon change has to keep those working too. Nothing else in the repository drives the addon's `TickDriver`, so the real-time game's Godot steps are the whole of its coverage.
- A behaviour change comes with a test; a language change comes with its entry in [docs/language.md](docs/language.md), and a change to the Godot node with its entry in [docs/godot.md](docs/godot.md).
- A change to [docs/quickstart.md](docs/quickstart.md) keeps the page runnable. CI follows it word for word in an empty folder against packages packed from the branch, on every push and pull request, and the release workflow runs the same check again against the packages it is about to publish. The blocks marked `<!-- smoke: run -->` are run in order, and the ones marked `<!-- smoke: file <path> -->` are written to that path; both are HTML comments, so a rendered page shows no sign of them.
  ```
  dotnet pack src/Cantrip.Core -c Release -o /tmp/feed
  dotnet pack src/Cantrip.Cli -c Release -o /tmp/feed
  bash tools/quickstart-smoke.sh /tmp/feed
  ```
  The script also checks what the page states only in prose: the `Program.cs` the reader pastes compiles, `dotnet cantrip test content` reports `3 passed, 0 failed`, and the battle ends before the input runs out. So a fourth test in the tests block, or content that changes how the fight goes, fails the build with the page still reading correctly. The content block is tied to `docs/godot.md` as well, which [Working on the Godot addon](#working-on-the-godot-addon) below describes.
- A change to a public signature, or to the documentation comment on one, in Cantrip.Core or in the addon regenerates the API reference. `bash tools/api-docs.sh` writes `docs/api/`, and the result is committed with the change; CI runs `bash tools/api-docs.sh --check` and fails when what is committed disagrees with the sources. Nothing local runs it for you.
- A change to Cantrip.Core also survives being published. `bash tools/publish-check.sh` publishes the reference game trimmed, and with Native AOT where the machine has a platform linker, with the trim and AOT analysers on and every one of their warnings an error, then plays eight whole runs with a save and a restore in each, in the binary that came out. Reflection, `System.Text.Json` and dynamic dispatch pass every other test here and die there. Both scripts are bash; on Windows they run in the bash that comes with Git.
- Anything that changes results keeps determinism: no floating point, `System.Random` or hash-order dependence in the rules.
- A change to the public C# API of Cantrip.Core is written into `src/Cantrip.Core/PublicAPI.Unshipped.txt`, in the analyser's own format: the signature with its `!` and `?` nullability marks and its return type, and for a removal that same line prefixed `*REMOVED*`. The build error names the symbol rather than the line, so copy the shape from the lines already in the file, or let an IDE's **Add public types and members to the declared API** fix write it. The build fails until the line is there, so no API change slips through unnoticed. The error is RS0016 for an addition, RS0017 for a removal or for a changed signature, which is a `*REMOVED*` line and a new line together. `PublicAPI.Shipped.txt` is the surface the last release shipped; only a release edits it, and a pull request never does.

  Since 1.0 that surface is a promise: [docs/stability.md](docs/stability.md) says no public type, member or signature of Cantrip.Core changes without a 2.0. An addition can still go into a 1.x release, and [What a 1.x release may add](docs/stability.md#what-a-1x-release-may-add) says which additions are safe. A `*REMOVED*` line is not one of them. It means a member has gone or its signature has changed, so open an issue before you write one.
- Add a line to the unreleased section of [CHANGELOG.md](CHANGELOG.md) for anything a user would notice, under **Breaking changes**, **Save format**, **Same-seed results** or **Bot behaviour** if it changes one of those. A change to the Godot node's methods, signals or dictionary keys counts as breaking, like one to the C# API.

[docs/architecture.md](docs/architecture.md) explains how the library fits together and has a checklist for adding a new kind of syntax.

By contributing you agree that your contribution is licensed under the [MIT license](LICENSE).

## Working on the Godot addon

Building the demo, the headless Godot tests, the install test in a blank project, and packaging the addon are described in [Working on the addon](docs/godot.md#working-on-the-addon) at the end of the Godot guide.

The install test, `tools/godot-install-smoke.sh`, also holds the Godot guide to the quickstart. It fails unless the `content/game.cantrip` block in `docs/godot.md` is byte for byte the one in `docs/quickstart.md`, and unless the first battle prints what `docs/godot.md` shows for its first turn and still ends with the Ghoul falling on turn 3. So a change to the quickstart's content means the same change in `docs/godot.md`, and a fresh copy of the first-turn output printed there.

## Releasing (maintainer)

1. Set the new version everywhere it is written. A test (`AddonVersionTests`, in `tests/Cantrip.Godot.Tests`) fails while any of these differ:
   - `Directory.Build.props`: `VersionPrefix`, which the packages take their version from;
   - `godot/Cantrip.Demo/addons/cantrip/plugin.cfg`: `version=`, which also names the addon's zip;
   - the `dotnet add package Cantrip.Core --version ...` line in `docs/godot.md` and in `godot/Cantrip.Demo/addons/cantrip/README.md`.

   The README, the addon repository's README and the NuGet pages name no version, so they need nothing.
2. Rename the changelog's `[Unreleased]` section to the version and date, and start a new empty `[Unreleased]` above it. Check that it says what changed under **Breaking changes**, **Save format**, **Same-seed results** and **Bot behaviour**, and update the known limitations in `docs/stability.md`.
3. Move the lines of `src/Cantrip.Core/PublicAPI.Unshipped.txt` to the end of `PublicAPI.Shipped.txt`, leaving only `#nullable enable` in Unshipped. A `*REMOVED*` line does not move: the analyser refuses a shipped file that has removed members. It deletes the line it names from Shipped and is then dropped, or is dropped on its own where that member was added and removed inside the same cycle and never shipped.
4. Commit and push, then tag that commit with the version and push the tag: `git tag v<version>` and `git push origin v<version>`.

The tag starts `.github/workflows/release.yml`. It refuses a tag that does not match the version, and refuses to start while either repository secret below is missing. It runs the whole CI workflow at the tagged commit (both platforms, the simulations and the Godot job), packs both packages, and follows the quickstart against them in an empty folder. Only then, in a separate job that runs no build or test code, does it publish:

- the packages to nuget.org, through Trusted Publishing: a policy on nuget.org for this repository and `release.yml`, plus a `NUGET_USER` repository secret holding the nuget.org profile name. No API key is stored.
- the GitHub release, with the packages and the Godot addon zip, taking the notes from the changelog.
- the addon to [AGomnes/cantrip-godot](https://github.com/AGomnes/cantrip-godot), which holds `addons/cantrip` at its root, the layout the Godot Asset Library installs from. The release replaces its `addons` folder with the new addon, copies in `tools/addon-repo/README.md` and `.gitattributes`, commits, and tags the commit with the same version. This needs the `ADDON_REPO_TOKEN` repository secret: a fine-grained token with read and write access to Contents on that one repository.

A package on nuget.org cannot be deleted, only unlisted, so everything that can fail runs before that job. A re-run after a failure in the publishing job is safe: packages already on nuget.org are skipped, an existing GitHub release has its files refreshed, and an addon repository that already has the tag is left alone.
