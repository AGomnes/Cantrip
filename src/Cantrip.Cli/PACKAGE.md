# Cantrip.Cli

The `cantrip` command-line tool for [Cantrip](https://github.com/AGomnes/Cantrip) content: check it, lint it, run its tests, play it many times with a bot, print its rules text, and try statements against a live game. It is built for .NET 9 and runs on .NET 9 or later.

Install it into your project's folder, then run it as `dotnet cantrip`:

```
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease

dotnet cantrip validate content
dotnet cantrip lint content
dotnet cantrip test content
dotnet cantrip sim content
dotnet cantrip describe content
dotnet cantrip repl content
dotnet cantrip --version
```

| Command | What it does | Options |
|---|---|---|
| `validate <path>...` | Loads the content and reports its errors, including the linter's errors such as an unknown verb | `--suppress <codes>` |
| `lint <path>...` | Reports what `validate` does, plus likely mistakes as warnings, such as a name nothing defines, an event nothing raises, a listener written without `on`, or rules text that has drifted from its effect | `--suppress <codes>`; `--warnings-as-errors` fails on a warning as well as an error |
| `test <path>...` | Runs the `test` blocks in the content | `--filter <text>` runs only the tests whose name contains the text; `--trace` prints the causality trace of each failing test, with what its `log` statements wrote |
| `sim <path>...` | Plays the `scenario` blocks many times with a bot and reports what the content allowed: a run that threw, a battle that never ended, a card that was never playable, an enemy move that never fired | `--name <text>`, `--runs N`, `--seed S`, `--turn-limit N`, `--watch SEED`, `--suppress <codes>` |
| `describe <path>...` | Prints the generated rules text of every definition | `--name <name>` prints only that one |
| `repl <path>...` | Starts a game from the content and runs each statement you type as the player, against a 100 hp Dummy. `:state` shows the game, `:trace` the causality trace, `:quit` leaves | |
| `--version` | Prints the version and the commit it was built from | |
| `--help` | Prints a summary of the commands and options | |

A path is a file or a folder; a folder loads every `.cantrip` file under it. `--suppress` takes comma-separated diagnostic codes, such as `CT306,CT310`, or `CT301` for verbs your game registers in C#. It leaves out warnings and notes from loading the files too, but never an error from loading.

Exit codes: 0 success, 1 content errors, failing tests, or a `sim` run that threw, stalled or failed an expectation, 2 bad usage. `lint` fails only on errors, and prints its warnings but still exits 0, unless it is given `--warnings-as-errors`: then a warning fails it too, which is what a build server wants. Notes never fail it.

Each message starts with the file, line and column, then its level and a code such as `CT302`. [Diagnostics](https://github.com/AGomnes/Cantrip/blob/main/docs/language.md#diagnostics) in the language reference lists every code, with what it means and the usual fix. [Simulating](https://github.com/AGomnes/Cantrip/blob/main/docs/simulating.md) covers `sim`: what a scenario says, what the report measures and what it refuses to claim.

This is a **preview**; see [stability](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md). Start with the [quickstart](https://github.com/AGomnes/Cantrip/blob/main/docs/quickstart.md). Writing content rather than code? Start with [Writing content](https://github.com/AGomnes/Cantrip/blob/main/docs/writing-content.md), which needs no C#.
