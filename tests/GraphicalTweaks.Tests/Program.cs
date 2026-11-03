using System;
using GraphicalTweaks;
static class Program
{
    static void Main()
    {
        var m = new ShadowDistanceModel(150.5f);
        Assert(m.EffectiveDistance == 150.5f && !m.Enabled, "preserves exact baseline");
        Assert(!m.TrySet(float.NaN) && !m.TrySet(float.PositiveInfinity), "reject non-finite");
        Assert(!m.TrySet(0) && !m.TrySet(-1) && !m.TrySet(m.Maximum + 1), "reject out of bounds");
        Assert(m.TrySet(900) && m.Enabled && m.EffectiveDistance == 900, "set enables override");
        Assert(!m.TrySet(0) && m.EffectiveDistance == 900, "invalid input keeps previous");
        m.Reset();
        Assert(!m.Enabled && m.EffectiveDistance == 150.5f, "reset restores exact baseline");
        Assert(m.TrySet(100) && m.EffectiveDistance == 100, "edit after reset");
        bool failed = false; try { new ShadowDistanceModel(float.NaN); } catch (ArgumentOutOfRangeException) { failed = true; }
        Assert(failed, "invalid baseline fails explicitly");
        Console.WriteLine("PASS: 8 shadow-distance behavior checks");
        var decals = new DecalRangeModel();
        Assert(decals.Percent == 100, "stock decal default");
        Assert(!decals.TrySet(99) && !decals.TrySet(1001), "decal bounds validation");
        Assert(decals.TrySet(200) && decals.ScaleSize(2) == 4, "decal culling capped at tested 2x size");
        Assert(!decals.TrySet(-1) && decals.Percent == 200, "invalid decal input preserves setting");
        Assert(decals.TrySet(150) && decals.ScaleSize(2) == 3, "recalculate from original rather than compound");
        Assert(Math.Abs(decals.ProjectionPadding - 0.005f) < 0.000001f, "partial visibility adds half projection tolerance");
        decals.TrySet(200);
        Assert(decals.ProjectionPadding == 0.01f, "maximum adds only 1cm per normal side");
        Assert(decals.TrySet(100) && decals.ScaleSize(2) == 2, "decal exact reset");
        Assert(decals.ScaleSize(0) == 0, "zero dimension preserved");
        Assert(decals.ProjectionPadding == 0, "stock restores exact projection depth");
        Assert(DecalRangeModel.NormalizeSaved(1000) == 1000 && DecalRangeModel.NormalizeSaved(500) == 500, "preserve requested extended range");
        Assert(DecalRangeModel.NormalizeSaved(-1) == 100 && DecalRangeModel.NormalizeSaved(150) == 150, "corrupt config resets while valid config survives");
        Console.WriteLine("PASS: 12 decal-range behavior checks");
        Assert(decals.TrySetMultiplier(10) && decals.Multiplier == 10, "accept 10x multiplier");
        Assert(decals.AdaptiveScale(20) == 1, "large on-screen decals retain stock bounds at 10x");
        Assert(decals.AdaptiveScale(0.5f) == 9, "small decals grow just to 4.5 pixels");
        Assert(decals.AdaptiveScale(0.2f) == 10, "requested 10x limits distant eligibility");
        Assert(decals.AdaptiveScale(float.NaN) == 2 && decals.AdaptiveScale(0) == 2, "invalid projection uses bounded fallback");
        Assert(!decals.TrySetMultiplier(float.NaN) && !decals.TrySetMultiplier(float.PositiveInfinity) && !decals.TrySetMultiplier(0.9f) && !decals.TrySetMultiplier(10.1f) && decals.Multiplier == 10, "invalid multiplier retains selection");
        Assert(decals.TrySetMultiplier(1.25f) && decals.Multiplier == 1.3f, "numeric multiplier rounds to common tenth step");
        Assert(decals.TrySetMultiplier(1) && decals.AdaptiveScale(0.1f) == 1 && decals.ProjectionPadding == 0, "1x restores stock culling and projection");
        Assert(DecalRangeModel.NormalizeMultiplier(1.5f) == 1.5f && DecalRangeModel.NormalizeMultiplier(float.NaN) == 1 && DecalRangeModel.NormalizeMultiplier(12) == 10, "saved multiplier migration and validation");
        Console.WriteLine("PASS: 9 adaptive multiplier behavior checks");
        float offAxis = ProjectionFootprintModel.MinimumPixels(new ProjectionVector(5,0,-10), new ProjectionVector(.001f,0,0), new ProjectionVector(0,1,0), new ProjectionVector(0,0,1), 1000,1000);
        Assert(Math.Abs(offAxis - 101.212f) < 0.002f, "off-axis thin box includes perspective depth contribution");
        decals.TrySetMultiplier(10);
        Assert(decals.AdaptiveScale(offAxis) == 1, "already-large off-axis box does not inflate toward camera plane");
        Assert(float.IsPositiveInfinity(ProjectionFootprintModel.MinimumPixels(new ProjectionVector(0,0,-1), new ProjectionVector(1,0,0), new ProjectionVector(0,1,0), new ProjectionVector(0,0,2),1000,1000)), "near-plane crossing keeps native conservative bounds");
        Assert(ProjectionFootprintModel.MinimumPixels(new ProjectionVector(0,0,-100),new ProjectionVector(.025f,0,0),new ProjectionVector(0,.025f,0),new ProjectionVector(0,0,.025f),1000,1000) == .5f, "small frontal footprint tracks actual distance");
        Console.WriteLine("PASS: 4 perspective footprint checks");
        var resolution = new ShadowResolutionModel();
        Assert(resolution.Choice == 0 && resolution.RequestedLightResolution == null && resolution.RequestedPixelResolution == null, "shadow resolution follows game by default");
        Assert(resolution.TrySet(1) && resolution.RequestedLightResolution == 0, "low sun resolution enum mapping");
        Assert(resolution.RequestedPixelResolution == 512, "first explicit sun shadow pixel target");
        Assert(resolution.TrySet(3) && resolution.RequestedLightResolution == 2, "high sun resolution enum mapping");
        Assert(resolution.TrySet(4) && resolution.RequestedLightResolution == 3, "very-high sun resolution enum mapping");
        Assert(resolution.RequestedPixelResolution == 4096, "last explicit sun shadow pixel target");
        Assert(!resolution.TrySet(-1) && !resolution.TrySet(5) && resolution.Choice == 4, "invalid resolution preserves prior selection");
        resolution.Reset();
        Assert(resolution.Choice == 0 && resolution.RequestedLightResolution == null && resolution.RequestedPixelResolution == null, "resolution reset follows original light settings");
        Assert(ShadowResolutionModel.NormalizeSaved(9) == 0 && ShadowResolutionModel.NormalizeSaved(3) == 3, "invalid saved resolution normalizes to default");
        Console.WriteLine("PASS: 9 shadow-resolution checks");
    }
    static void Assert(bool value, string label) { if (!value) throw new Exception(label); }
}
