using System.Runtime.CompilerServices;

namespace GraphicalTweaks
{
    internal static class RuntimeFactory
    {
        // Optional rendering types must resolve after entering the component,
        // rather than while Unity discovers and creates its script callbacks.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static IPluginRuntime Create(Plugin host) => new Runtime(host);
    }
}
