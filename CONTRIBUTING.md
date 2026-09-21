# Contributing

Thanks for looking. Cantrip is maintained by one person, so the most useful contributions are the ones that are easy to act on.

## Issues are the best way in

The [issue forms](https://github.com/AGomnes/Cantrip/issues/new/choose) ask for what each kind of report needs:

- **Bugs**: the smallest `.cantrip` file that shows the problem, what you expected, and what happened, plus the output of `dotnet cantrip --version` (in a Godot project without the tool, the version in `addons/cantrip/plugin.cfg` and the Cantrip.Core version in your `.csproj`). A failing `test` block is ideal, because it describes the problem and proves the fix at once.
- **Effects the language cannot express**: name the game and the card, relic or ability, and say what it does. That is how [docs/coverage.md](docs/coverage.md) grows, and it is the most valuable thing an outside user can tell this project.
- **Rough edges**: anything that confused you in the [quickstart](docs/quickstart.md) or the docs. If you had to guess, the docs are wrong.

## The repository

| Path | Contents |
|---|---|
| `src/Cantrip.Core` | The library: parser, content loading, rules engine, interpreter, linter, rules text, test runner |
| `src/Cantrip.Cli` | The `cantrip` tool |
| `src/Cantrip.Sim` | Plays runs of the sample roguelite with a bot and reports win rates. `dotnet run --project src/Cantrip.Sim -c Release -- --help` lists its options, such as `--runs N`, and `--watch SEED` to follow one run turn by turn |
| `samples/basic` | Small examples of most features, including the Fireball, Frozen and Kindling in the README, and the Strike, Defend and Jaw Worm its C# example uses |
| `samples/corpus` | Effects re-created from existing games |
| `samples/slice` | The sample roguelite: a witch climbing a tower, fire against frost, with a boss that changes its moves at half health |
| `samples/recipes` | The recipes from [docs/writing-content.md](docs/writing-content.md), with their tests |
| `godot/Cantrip.Demo` | The Godot addon, a demo and headless tests |
| `tests` | Unit tests for the library and for the addon's engine-free layer |
| `tools` | Packaging the addon, and checking the quickstart and the addon install against freshly built packages |

## Pull requests

Please open an issue first for anything beyond a small fix, so we can agree on the approach before you spend time on it. Then:

- `dotnet build Cantrip.sln` has no warnings, and `dotnet test tests/Cantrip.Core.Tests` and `dotnet test tests/Cantrip.Godot.Tests` pass.
- Lint and tests pass on each folder in `samples/`, each loaded on its own, with the tool run from source:
  ```
  dotnet run --project src/Cantrip.Cli -- lint samples/basic
  dotnet run --project src/Cantrip.Cli -- test samples/basic
  ```
- A behaviour change comes with a test, and a language change with its entry in [docs/language.md](docs/language.md).
- Anything that changes results keeps determinism: no floating point, `System.Random` or hash-order dependence in the rules.
- A change to the public C# API of Cantrip.Core is written into `src/Cantrip.Core/PublicAPI.Unshipped.txt`. The build warns until it is (RS0016 for an addition, RS0017 for a removal), so no API change slips through unnoticed.
- Add a line to the unreleased section of [CHANGELOG.md](CHANGELOG.md) for anything a user would notice, under **Breaking changes**, **Save format** or **Same-seed results** if it changes one of those. A change to the Godot node's methods, signals or dictionary keys counts as breaking, like one to the C# API.

[docs/architecture.md](docs/architecture.md) explains how the library fits together and has a checklist for adding a new kind of syntax.

By contributing you agree that your contribution is licensed under the [MIT license](LICENSE).

## Working on the Godot addon

Building the demo, the headless Godot tests, the install test in a blank project, and packaging the addon are described in [Working on the addon](docs/godot.md#working-on-the-addon) at the end of the Godot guide.

## Releasing (maintainer)

1. Set the new version everywhere it is written. A test (`AddonVersionTests`, in `tests/Cantrip.Godot.Tests`) fails while any of these differ:
   - `Directory.Build.props`: `VersionPrefix` and `VersionSuffix`, which the packages take their version from;
   - `godot/Cantrip.Demo/addons/cantrip/plugin.cfg`: `version=`, which also names the addon's zip;
   - the `dotnet add package Cantrip.Core --version ...` line in `docs/godot.md` and in `godot/Cantrip.Demo/addons/cantrip/README.md`.

   The README, the addon repository's README and the NuGet pages name no version, so they need nothing.
2. Rename the changelog's `[Unreleased]` section to the version and date, and start a new empty `[Unreleased]` above it. Check that it says what changed under **Breaking changes**, **Save format** and **Same-seed results**, and update the known limitations in `docs/stability.md`.
3. Move the lines of `src/Cantrip.Core/PublicAPI.Unshipped.txt` to the end of `PublicAPI.Shipped.txt`, leaving only `#nullable enable` in Unshipped.
4. Commit and push, then tag that commit with the version and push the tag: `git tag v<version>` and `git push origin v<version>`.

The tag starts `.github/workflows/release.yml`. It refuses a tag that does not match the version, and refuses to start while either repository secret below is missing. It runs the whole CI workflow at the tagged commit (both platforms, the slice simulation and the Godot job), packs both packages, and follows the quickstart against them in an empty folder. Only then, in a separate job that runs no build or test code, does it publish:

- the packages to nuget.org, through Trusted Publishing: a policy on nuget.org for this repository and `release.yml`, plus a `NUGET_USER` repository secret holding the nuget.org profile name. No API key is stored.
- the GitHub release, with the packages and the Godot addon zip, taking the notes from the changelog.
- the addon to [AGomnes/cantrip-godot](https://github.com/AGomnes/cantrip-godot), which holds `addons/cantrip` at its root, the layout the Godot Asset Library installs from. The release replaces its `addons` folder with the new addon, copies in `tools/addon-repo/README.md` and `.gitattributes`, commits, and tags the commit with the same version. This needs the `ADDON_REPO_TOKEN` repository secret: a fine-grained token with read and write access to Contents on that one repository.

A package on nuget.org cannot be deleted, only unlisted, so everything that can fail runs before that job. A re-run after a failure in the publishing job is safe: packages already on nuget.org are skipped, an existing GitHub release has its files refreshed, and an addon repository that already has the tag is left alone.
