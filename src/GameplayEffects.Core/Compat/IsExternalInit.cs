// Required so that C# 9 `init` accessors and records compile against netstandard2.1.
// The BCL only ships this type from .NET 5 onwards.

using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
