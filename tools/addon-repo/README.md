# Cantrip for Godot

The Godot 4 addon for [Cantrip](https://github.com/AGomnes/Cantrip): write the cards, statuses, relics, enemies and abilities of a turn-based card game as short `.cantrip` scripts, and drive battles from GDScript or C#.

**This repository is generated.** Each [Cantrip release](https://github.com/AGomnes/Cantrip/releases) copies its addon here and tags it with the same version. Report issues and send changes to [AGomnes/Cantrip](https://github.com/AGomnes/Cantrip), where the addon is developed and tested.

## Install

Requires the **.NET edition of Godot 4.6** and a C# project (Project → Tools → C# → Create C# solution), even if your game logic is GDScript.

1. Install the addon: from the Godot Asset Library, or copy `addons/cantrip/` from this repository (or the zip on a [release](https://github.com/AGomnes/Cantrip/releases)) into your project.
2. Add the rules engine at the same version as the addon, in the folder with your `.csproj`. The version is in `addons/cantrip/plugin.cfg`:
   ```
   dotnet add package Cantrip.Core --version <the version in plugin.cfg>
   ```
3. Build the C# project, then enable *Cantrip* in Project Settings → Plugins.
4. Put your `.cantrip` files in `res://content` and add a `CantripRuntime` node to a scene.

The full guide, with a battle in GDScript, player choices, saving and exports, is [docs/godot.md](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md) in the main repository.

## License

MIT, as in `addons/cantrip/LICENSE`.
