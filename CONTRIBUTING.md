# Contributing

Thanks for looking. Cantrip is maintained by one person, so the most useful contributions are the ones that are easy to act on.

## Issues are the best way in

The [issue forms](https://github.com/AGomnes/Cantrip/issues/new/choose) ask for what each kind of report needs:

- **Bugs**: the smallest `.cantrip` file that shows the problem, what you expected, and what happened, plus the output of `cantrip --version`. A failing `test` block is ideal, because it describes the problem and proves the fix at once.
- **Effects the language cannot express**: name the game and the card, relic or ability, and say what it does. That is how [docs/coverage.md](docs/coverage.md) grows, and it is the most valuable thing an outside user can tell this project.
- **Rough edges**: anything that confused you in the [quickstart](docs/quickstart.md) or the docs. If you had to guess, the docs are wrong.

## Pull requests

Please open an issue first for anything beyond a small fix, so we can agree on the approach before you spend time on it. Then:

- `dotnet build Cantrip.sln` has no warnings, and `dotnet test tests/Cantrip.Core.Tests` and `dotnet test tests/Cantrip.Godot.Tests` pass.
- `cantrip lint` and `cantrip test` pass on `samples/basic`, `samples/corpus` and `samples/slice`, each loaded on its own.
- A behaviour change comes with a test, and a language change with its entry in [docs/language.md](docs/language.md).
- Anything that changes results keeps determinism: no floating point, `System.Random` or hash-order dependence in the rules.
- A change to the public C# API of Cantrip.Core is written into `src/Cantrip.Core/PublicAPI.Unshipped.txt`. The build warns until it is (RS0016 for an addition, RS0017 for a removal), so no API change slips through unnoticed.
- Add a line to the unreleased section of [CHANGELOG.md](CHANGELOG.md) for anything a user would notice.

[docs/architecture.md](docs/architecture.md) explains how the library fits together and has a checklist for adding a new kind of syntax.

By contributing you agree that your contribution is licensed under the [MIT license](LICENSE).

## Releasing (maintainer)

1. Set the version in `Directory.Build.props` (`VersionPrefix`, `VersionSuffix`), and the same version in `godot/Cantrip.Demo/addons/cantrip/plugin.cfg` and in the `dotnet add package Cantrip.Core --version ...` line of both Godot install guides (the addon's README and `docs/godot.md`); a test fails while any of them differ.
2. Rename the changelog's `[Unreleased]` section to the version and date, and start a new empty `[Unreleased]` above it.
3. Move the lines of `src/Cantrip.Core/PublicAPI.Unshipped.txt` to the end of `PublicAPI.Shipped.txt`, leaving only `#nullable enable` in Unshipped.
4. Commit and push, then tag that commit and push the tag: `git tag v0.1.0-preview.1` and `git push origin v0.1.0-preview.1`.

The tag starts `.github/workflows/release.yml`. It refuses a tag that does not match the version, runs the whole CI workflow at the tagged commit (both platforms, the slice simulation and the Godot job), packs both packages, follows the quickstart against them in an empty folder, and only then, in a separate job that runs no build or test code, publishes to nuget.org through Trusted Publishing (a policy on nuget.org for this repository and `release.yml`, plus a `NUGET_USER` repository secret holding the nuget.org profile name; no API key is stored) and creates the GitHub release with the packages and the Godot addon zip, taking the notes from the changelog. A package on nuget.org cannot be deleted, only unlisted, so everything that can fail runs before that step.
