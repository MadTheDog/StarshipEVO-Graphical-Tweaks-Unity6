using System;
namespace GraphicalTweaks
{
    public sealed class ShadowDistanceModel
    {
        public float Baseline { get; }
        public float SelectedDistance { get; private set; }
        public float EffectiveDistance => Enabled ? SelectedDistance : Baseline;
        public bool Enabled { get; private set; }
        public int Maximum { get; }
        public ShadowDistanceModel(float baseline)
        {
            if (float.IsNaN(baseline) || float.IsInfinity(baseline) || baseline <= 0)
                throw new ArgumentOutOfRangeException(nameof(baseline));
            Baseline = baseline;
            Maximum = (int)Math.Min(100000, Math.Max(1000, Math.Ceiling((double)baseline * 10)));
            SelectedDistance = Math.Min(Maximum, baseline);
        }
        public bool TrySet(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 1 || value > Maximum) return false;
            SelectedDistance = value;
            Enabled = true;
            return true;
        }
        public void Reset() { Enabled = false; }
    }
}
