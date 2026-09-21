# Cantrip for Godot

Write cards, statuses, relics, enemies and abilities as `.cantrip` text files, and drive them from one
node. This folder is the addon; the rules engine it uses is a separate .NET library.

Requires **Godot 4.6 .NET**. The addon is C# source rather than a DLL, because Godot resolves
scripts by file path inside your project's own assembly.

## Installing

1. Copy `addons/cantrip/` into your project.
2. Add the rules engine from NuGet, in the folder with your `.csproj`. The version must match this
   addon's, which is also in `plugin.cfg`:
   ```
   dotnet add package Cantrip.Core --version 0.1.0-preview.2
   ```
   Offline, reference the DLL instead. The Cantrip.Core `.nupkg` attached to the GitHub release is
   a zip file: copy `lib/netstandard2.1/Cantrip.Core.dll` (and `Cantrip.Core.xml`, for editor help)
   out of it into a folder such as `lib/` beside your `.csproj`. It has no dependencies. Keep it
   out of `addons/cantrip/`, which an update replaces, and out of any `bin/` folder, which most
   `.gitignore` files leave out of your commits:
   ```xml
   <Reference Include="Cantrip.Core">
     <HintPath>lib/Cantrip.Core.dll</HintPath>
   </Reference>
   ```
3. **Build the C# project before enabling the plugin.** Until the assembly exists, Godot cannot
   load a C# plugin and every `[GlobalClass]` node is invisible, with nothing in the log to say why.
4. Enable *Cantrip* in Project Settings → Plugins.
5. Put your `.cantrip` files in `res://content`, add a `CantripRuntime` node to a scene, and
   point it at that folder.

If your project has no C# solution yet, create one first (Project → Tools → C#). Godot also needs a
solution file beside the project to export a .NET game at all.

## What you get

- A node that loads content, runs battles and hands everything to script as ids and dictionaries.
- A dock with problems (parse errors and lint findings), your content's own DSL tests, description
  previews with live values, and a source viewer with syntax highlighting.
- An importer so `.cantrip` files reach an exported build, and an export check that fails a build where
  they would not.
- A tab in the debugger showing a running game's causality tree, with hot reload and a console.

## Documentation

Full documentation, including the two rules GDScript imposes (members keep their C# PascalCase
names, and a C# default argument is not a default in GDScript), is in `docs/godot.md` in the
project repository: https://github.com/AGomnes/Cantrip

MIT licensed.
