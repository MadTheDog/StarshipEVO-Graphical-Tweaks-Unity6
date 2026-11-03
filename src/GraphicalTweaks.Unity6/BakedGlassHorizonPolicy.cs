namespace GraphicalTweaks
{
    internal static class BakedGlassHorizonPolicy
    {
        internal static bool Matches(bool customMode, string name, int width, int height, int mipCount, bool bc6h) =>
            customMode && name == "CustomReflectionProbe" && width == 128 && height == 128 && mipCount == 8 && bc6h;

        internal static int NormalizeSheen(int choice) => choice >= 0 && choice <= 3 ? choice : 0;

        internal static float SheenRadiance(int choice) => choice switch
        {
            1 => 0.06f,
            2 => 0.15f,
            3 => 0.30f,
            _ => 0f
        };
    }
}
