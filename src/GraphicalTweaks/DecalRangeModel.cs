namespace GraphicalTweaks
{
    public sealed class DecalRangeModel
    {
        public const int Maximum = 1000;
        public int Percent { get; private set; } = 100;
        public float Multiplier => Percent / 100f;
        public bool TrySetMultiplier(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 1 || value > 10) return false;
            return TrySet((int)System.Math.Round(value * 10, System.MidpointRounding.AwayFromZero) * 10);
        }
        public float AdaptiveScale(float minimumPixels)
        {
            if (Percent == 100) return 1;
            if (float.IsNaN(minimumPixels) || float.IsInfinity(minimumPixels) || minimumPixels <= 0) return System.Math.Min(2, Multiplier);
            float needed = (float)System.Math.Ceiling((4.5f / minimumPixels) * 8) / 8;
            return System.Math.Min(Multiplier, System.Math.Max(1, needed));
        }
        public static float NormalizeMultiplier(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 1;
            return (float)System.Math.Round(System.Math.Max(1, System.Math.Min(10, value)) * 10, System.MidpointRounding.AwayFromZero) / 10;
        }
        public float ProjectionPadding => 0.01f * ((Percent - 100) / 100f);
        public static int NormalizeSaved(int percent) => percent < 100 ? 100 : System.Math.Min(percent, Maximum);
        public bool TrySet(int percent)
        {
            if (percent < 100 || percent > Maximum) return false;
            Percent = percent;
            return true;
        }
        public float ScaleSize(float original) => original * (Percent / 100f);
    }
}
