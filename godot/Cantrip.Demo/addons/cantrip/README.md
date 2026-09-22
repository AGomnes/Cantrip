# Cantrip for Godot

Write cards, statuses, relics, enemies and abilities as `.cantrip` text files, and drive them from one
node, in GDScript or C#. This folder is the addon; the rules engine it uses is a separate .NET library.

Requires **Godot 4.6.x, .NET edition** (tested on 4.6.1 and 4.6.2) and the **.NET SDK 8 or later**,
even for a game written entirely in GDScript. The addon is C# source rather than a DLL, because
Godot resolves scripts by file path inside your project's own assembly.

## Installing

The full install guide, with what to check first and what to expect at each step, is
https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md. In short:

1. If your project has no C# solution, create one: Project → Tools → C# → Create C# solution.
2. Copy `addons/cantrip/` into your project. Until step 3, a build fails with errors about the
   `Cantrip` namespace; that is expected.
3. Add the rules engine from NuGet, in the folder with your `.csproj`. The version must match this
   addon's, which is also in `plugin.cfg`:
   ```
   dotnet add package Cantrip.Core --version 0.1.0-preview.3
   ```
   Offline, reference the `Cantrip.Core.dll` from the `.nupkg` on the GitHub release instead; the
   guide shows how.
4. **Build the C# project before enabling the plugin**, with the editor's Build button or
   `dotnet build`. Until the assembly exists, Godot cannot load a C# plugin and the addon's nodes
   are missing, with nothing in the log to say why.
5. Enable *Cantrip* in Project Settings → Plugins.
6. Put your `.cantrip` files in `res://content` and add a `CantripRuntime` node to a scene.

## What you get

- A node that loads content, runs battles and hands everything to script as ids and dictionaries.
- A dock with problems (parse errors and lint findings), your content's own tests, description
  previews with live values, and a source viewer with syntax highlighting.
- An importer so `.cantrip` files reach an exported build, and an export check that fails a build where
  they would not.
- Two tabs in the debugger: a running game's causality trace, with pause and step, and the entities
  in play.

## Documentation

The guide, https://github.com/AGomnes/Cantrip/blob/main/docs/godot.md, has a first battle in
GDScript that runs as written, and a reference for every method, signal and dictionary of the node.
Two rules to know before writing any GDScript against it: members keep their C# PascalCase names,
and a C# default argument is not a default in GDScript, so every parameter must be passed.

MIT licensed.
