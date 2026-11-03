namespace SEVO.GlassRenderingProbe
{
    internal enum GlassIdenticalOutcome { Inconclusive, Different, Equivalent }
    internal static class GlassComparisonOutcome
    {
        // Tiny or empty draws cannot establish native interpolation parity.
        internal static GlassIdenticalOutcome Identical(int covered,int altered,int nonfinite,int alphaOutOfRange,
            bool requireColor=false,int colored=0)
        {
            if(altered!=0||nonfinite!=0||alphaOutOfRange!=0)return GlassIdenticalOutcome.Different;
            if(covered<16||(requireColor&&colored<16))return GlassIdenticalOutcome.Inconclusive;
            return GlassIdenticalOutcome.Equivalent;
        }
    }
}
