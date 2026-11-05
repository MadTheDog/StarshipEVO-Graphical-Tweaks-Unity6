using System;

namespace GraphicalTweaks
{
    [Serializable]
    internal sealed class NativeFeatureContract
    {
        public string name = "";
        public NativeContractEntry[] entries = Array.Empty<NativeContractEntry>();
    }

    [Serializable]
    internal sealed class NativeContractEntry
    {
        public string kind = "", type = "", member = "", hash = "";
    }
}
