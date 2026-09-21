# Cantrip.Cli

The `cantrip` command-line tool for [Cantrip](https://github.com/AGomnes/Cantrip) content: check it, lint it, run its tests, print its rules text, and try statements against a live game. It is built for .NET 9 and runs on .NET 9 or later.

Install it into your project's folder, then run it as `dotnet cantrip`:

```
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease

dotnet cantrip validate content
dotnet cantrip lint content
dotnet cantrip test content
dotnet cantrip describe content
dotnet cantrip repl content
dotnet cantrip --version
```

| Command | What it does | Options |
|---|---|---|
| `validate <path>...` | Loads the content and reports its errors, including the linter's errors such as an unknown verb | `--suppress <codes>` |
| `lint <path>...` | Reports what `validate` does, plus likely mistakes as warnings, such as a name nothing defines, an event nothing raises, or rules text that has drifted from its effect | `--suppress <codes>` |
| `test <path>...` | Runs the `test` blocks in the content | `--filter <text>` runs only the tests whose name contains the text; `--trace` prints the causality trace of each failing test |
| `describe <path>...` | Prints the generated rules text of every definition | `--name <name>` prints only that one |
| `repl <path>...` | Starts a game from the content and runs each statement you type as the player, against a 100 hp Dummy. `:state` shows the game, `:trace` the causality trace, `:quit` leaves | |
| `--version` | Prints the version and the commit it was built from | |
| `--help` | Prints a summary of the commands and options | |

A path is a file or a folder; a folder loads every `.cantrip` file under it. `--suppress` takes comma-separated diagnostic codes, such as `CT306,CT310`, or `CT301` for verbs your game registers in C#.

Exit codes: 0 success, 1 content errors or failing tests, 2 bad usage. `lint` fails only on errors; its warnings are printed but still exit 0.

This is a **preview**; see [stability](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md). Start with the [quickstart](https://github.com/AGomnes/Cantrip/blob/main/docs/quickstart.md).
