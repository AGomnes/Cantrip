# Cantrip for Godot

The Godot 4 addon for [Cantrip](https://github.com/AGomnes/Cantrip): write the cards, statuses, relics, enemies and abilities of a turn-based card game as short `.cantrip` scripts, and drive battles from GDScript or C#.

**This repository is generated.** Each [Cantrip release](https://github.com/AGomnes/Cantrip/releases) copies its addon here and tags it with the same version. Report issues and send changes to [AGomnes/Cantrip](https://github.com/AGomnes/Cantrip), where the addon is developed and tested.

## Install

You need the **.NET edition of Godot 4.6.x** (tested on 4.6.1 and 4.6.2) and the **.NET SDK 8 or later**, even if your game is all GDScript; you will not write any C#. A GDScript-only project first needs a C# solution: Project → Tools → C# → Create C# solution.

1. Install the addon: from the Godot Asset Library (the listing is awaiting review), or copy `addons/cantrip/` from this repository or from the zip on a [release](https://github.com/AGomnes/Cantrip/releases) into your project. Until step 2, a build fails with errors about the `Cantrip` namespace; that is expected.
2. Add the rules engine at the same version as the addon, in the folder with your `.csproj`. The version is in `addons/cantrip/plugin.cfg`:
   ```
   dotnet add package Cantrip.Core --version <the version in plugin.cfg>
   ```
3. Build the C# project, with the editor's Build button or `dotnet build`, then enable *Cantrip* in Project Settings → Plugins.
4. Put your `.cantrip` files in `res://content` and add a `CantripRuntime` node to a scene.

The full guide is [docs/godot.md](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md) in the main repository: installing step by step, a first battle in GDScript that runs as written, a reference for every method of the node and every dictionary it returns, and player choices, saving and exports.

## License

MIT, as in `addons/cantrip/LICENSE`.
