#if TOOLS
using Godot;

namespace GameplayEffects.GodotAdapter
{
    /// <summary>
    /// The editor half of the addon. Everything here is compiled out of an exported game: the
    /// Godot SDK only defines TOOLS (and only references the editor assembly) in the Debug
    /// configuration, so without this guard an export would not compile at all.
    /// </summary>
    /// <remarks>
    /// The [Tool] attribute is not optional either. Without it the plugin silently never runs:
    /// the build succeeds, Godot logs nothing, and _EnterTree is simply never called.
    /// </remarks>
    [Tool]
    public partial class GameplayEffectsPlugin : EditorPlugin
    {
        public override string _GetPluginName() => "Gameplay Effects";

        public override void _EnterTree()
        {
        }

        public override void _ExitTree()
        {
        }
    }
}
#endif
