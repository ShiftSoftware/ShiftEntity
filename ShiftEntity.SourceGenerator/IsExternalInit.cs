// Polyfill so records/init-only setters compile on netstandard2.0 (analyzer TFM).
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
