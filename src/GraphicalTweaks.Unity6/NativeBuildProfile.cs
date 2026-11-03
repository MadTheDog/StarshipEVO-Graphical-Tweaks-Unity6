using System;

namespace GraphicalTweaks
{
    internal static class NativeBuildProfile
    {
        internal static readonly Guid ValidatedAssembly = new Guid("00859162-dd57-4798-be35-54252142455d");
        internal static bool IsSupported(Guid actual) => actual == ValidatedAssembly;
    }
}
