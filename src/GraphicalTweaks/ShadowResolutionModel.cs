namespace GraphicalTweaks
{
    public sealed class ShadowResolutionModel
    {
        public int Choice { get; private set; }
        public int? RequestedLightResolution => Choice == 0 ? (int?)null : Choice - 1;
        public int? RequestedPixelResolution => Choice == 0 ? (int?)null : 512 << (Choice - 1);
        public bool TrySet(int choice)
        {
            if (choice < 0 || choice > 4) return false;
            Choice = choice;
            return true;
        }
        public void Reset() { Choice = 0; }
        public static int NormalizeSaved(int choice) => choice >= 0 && choice <= 4 ? choice : 0;
    }
}
