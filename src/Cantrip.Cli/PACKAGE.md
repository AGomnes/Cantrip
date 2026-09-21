# Cantrip.Cli

The `cantrip` command-line tool for [Cantrip](https://github.com/AGomnes/Cantrip) content: check it, lint it, run its tests, print its rules text, and try statements against a live game.

```
dotnet new tool-manifest
dotnet tool install Cantrip.Cli --prerelease

dotnet cantrip test content        # run the test blocks in every .cantrip file under content/
dotnet cantrip lint content        # unknown names, events nothing raises, text that drifted from its effect
dotnet cantrip describe content    # generated rules text for every definition
dotnet cantrip repl content        # run statements against a live game
```

Exit codes: 0 success, 1 content errors or failing tests, 2 bad usage, so it drops straight into CI.

This is a **preview**; see [stability](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md). Start with the [quickstart](https://github.com/AGomnes/Cantrip/blob/main/docs/quickstart.md).
