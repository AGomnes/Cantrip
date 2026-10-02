# Cantrip for Godot

The Godot 4 addon for [Cantrip](https://github.com/AGomnes/Cantrip): write the cards, heroes, statuses, relics, enemies and abilities of a single-player fight as short `.cantrip` scripts, and drive battles from GDScript or C#. Neither cards nor turns are required: a game whose party stands on a board and uses abilities on cooldowns against waves on a tick clock is the same addon with the piles left out.

**This repository is generated.** Each [Cantrip release](https://github.com/AGomnes/Cantrip/releases) copies its addon here and tags it with the same version, so the default branch is always the newest release and `addons/cantrip/plugin.cfg` says which one. Report issues and send changes to [AGomnes/Cantrip](https://github.com/AGomnes/Cantrip), where the addon is developed and tested.

From 1.0 what you write against is a promise: the `CantripRuntime` node's methods, signals, signal arguments and exported properties, the keys of every dictionary it returns and the words inside those dictionaries do not change without a 2.0, and content that loads and runs in 1.0 loads and runs in every 1.x. [Stability](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md) is the whole of what is frozen and what is not.

## What a card looks like

```
card Fireball
  cost 2
  target enemy
  tags attack, fire
  effect:
    deal 6 to target
    deal 2 to adjacent(target)
    if target.dead: draw 1
  text: "Hurl a ball of flame for {damage} damage. Kill it to draw {draw}."
```

`{damage}` is the first amount the card deals, shown with whatever modifiers apply at the moment; leave `text:` out and Cantrip writes the rules text itself. Heroes, statuses, relics, enemies and abilities are declared the same way, and the [language reference](https://github.com/AGomnes/Cantrip/blob/main/docs/language.md) has the rest.

## What you get

- A node that loads content, runs battles and hands everything to script as ids and dictionaries.
- A dock with problems (parse errors and lint findings), your content's own tests, description previews with live values, and an editor with syntax highlighting that checks what you type and saves with Ctrl+S.
- An importer so `.cantrip` files reach an exported build, and an export check that fails a build where they would not.
- Two tabs in the debugger: a running game's causality trace, with pause and step, and the entities in play.

## Install

You need the **.NET edition of Godot 4.6.x** (tested on 4.6.1 and 4.6.2) and the **.NET SDK 8 or later**, even if your game is all GDScript; you will not write any C#. A GDScript-only project first needs a C# solution: Project → Tools → C# → Create C# solution.

Exporting a .NET game has limits of Godot's own, and a game on the .NET edition is a .NET game however it is written: not to the web at all, and to Android and iOS only experimentally, as [C# platform support](https://docs.godotengine.org/en/4.6/tutorials/scripting/c_sharp/index.html#c-platform-support) in Godot's documentation explains. With Cantrip, only a Linux x64 export has been tried; [Platforms](https://github.com/AGomnes/Cantrip/blob/main/docs/stability.md#platforms) lists what has and has not.

1. Install the addon: search for Cantrip in the editor's AssetLib tab, or, if it is not listed yet, copy `addons/cantrip/` from this repository or from the zip on a [release](https://github.com/AGomnes/Cantrip/releases) into your project. Until step 2, a build fails with errors about the `Cantrip` namespace; that is expected.
2. Add the rules engine at the same version as the addon, in the folder with your `.csproj`. The version is in `addons/cantrip/plugin.cfg`:
   ```
   dotnet add package Cantrip.Core --version <the version in plugin.cfg>
   ```
3. Build the C# project, with the editor's Build button or `dotnet build`, then enable *Cantrip* in Project Settings → Plugins.
4. Put your `.cantrip` files in `res://content` and add a `CantripRuntime` node to a scene.

The full guide is [docs/godot.md](https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md) in the main repository: installing step by step, a first battle in GDScript that runs as written, a reference for every method of the node and every dictionary it returns, and player choices, rewards and upgrades between battles, saving and exports. [Troubleshooting](https://github.com/AGomnes/Cantrip/blob/main/docs/troubleshooting.md#godot) is what to read when one of those does not go as written.

## License

MIT, as in `addons/cantrip/LICENSE`.
