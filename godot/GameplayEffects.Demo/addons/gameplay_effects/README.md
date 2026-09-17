# Gameplay Effects for Godot

Write cards, statuses, relics, enemies and abilities as `.ge` text files, and drive them from one
node. This folder is the addon; the rules engine it uses is a separate .NET library.

Requires **Godot 4.6 .NET**. The addon is C# source rather than a DLL, because Godot resolves
scripts by file path inside your project's own assembly.

## Installing

1. Copy `addons/gameplay_effects/` into your project.
2. Reference the rules engine from your project's `.csproj`. It is not published as a package yet,
   so today that means the library itself:
   ```xml
   <ProjectReference Include="path/to/GameplayEffects.Core.csproj" />
   ```
   or a copy of its DLL:
   ```xml
   <Reference Include="GameplayEffects.Core">
     <HintPath>addons/gameplay_effects/bin/GameplayEffects.Core.dll</HintPath>
   </Reference>
   ```
3. **Build the C# project before enabling the plugin.** Until the assembly exists, Godot cannot
   load a C# plugin and every `[GlobalClass]` node is invisible, with nothing in the log to say why.
4. Enable *Gameplay Effects* in Project Settings → Plugins.
5. Put your `.ge` files in `res://content`, add a `GameplayEffectsRuntime` node to a scene, and
   point it at that folder.

If your project has no C# solution yet, create one first (Project → Tools → C#). Godot also needs a
solution file beside the project to export a .NET game at all.

## What you get

- A node that loads content, runs battles and hands everything to script as ids and dictionaries.
- A dock with problems (parse errors and lint findings), your content's own DSL tests, description
  previews with live values, and a source viewer with syntax highlighting.
- An importer so `.ge` files reach an exported build, and an export check that fails a build where
  they would not.
- A tab in the debugger showing a running game's causality tree, with hot reload and a console.

## Documentation

Full documentation, including the two rules GDScript imposes (members keep their C# PascalCase
names, and a C# default argument is not a default in GDScript), is in `docs/godot.md` in the
project repository: https://github.com/AGomnes/DSL

MIT licensed.
