# Cantrip.Cli

The `cantrip` command-line tool for [Cantrip](https://github.com/AGomnes/Cantrip) content: check it, lint it, run its tests, print its rules text, and try statements against a live game.

```
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease

dotnet cantrip test content
dotnet cantrip lint content
dotnet cantrip describe content
dotnet cantrip repl content
```

- `test` runs the test blocks in every `.cantrip` file under `content/`.
- `lint` reports likely mistakes: unknown names, events nothing raises, rules text that drifted from its effect.
- `describe` prints the generated rules text for every definition.
- `repl` starts a game from the content and lets you try statements in it.

Exit codes: 0 success, 1 content errors or failing tests, 2 bad usage. `lint` fails only on errors; its warnings are printed but still exit 0.

This is a **preview**; see [stability](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md). Start with the [quickstart](https://github.com/AGomnes/Cantrip/blob/main/docs/quickstart.md).
