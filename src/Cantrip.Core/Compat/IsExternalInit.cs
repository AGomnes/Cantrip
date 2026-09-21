// Required so that C# 9 `init` accessors and records compile against netstandard2.1.
// The BCL ships this type from .NET 5 onwards, where defining it again is a duplicate-type error,
// so the shim compiles itself out on modern targets. That keeps the option of embedding these
// sources directly in a net8.0 project, which is how a Godot addon would vendor the engine.

#if !NET5_0_OR_GREATER
using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
#endif
