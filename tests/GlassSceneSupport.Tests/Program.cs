using System;
using GraphicalTweaks;

static class Program
{
    private static int checks;
    private static void Require(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    static void Main()
    {
        Require(GlassSceneSupportModel.IsGeometrySupported(2, 1, 0, 0, out _),
            "Native-proven vertex1 keeps known base geometry and must remain eligible");
        Require(GlassSceneSupportModel.IsGeometrySupported(20, 1, 0, 0, out _, 11),
            "Native-proven squared hologram base supports both dynamic material selectors");
        foreach (int alpha in new[] {2, 5, 7, 9, 0, 20, 123})
            for (int vertex = 0; vertex <= 7; vertex++)
                for (int resize = 0; resize <= 8; resize++)
                    foreach (int normal in new[] {0, 1})
                    {
                        bool supported = GlassSceneSupportModel.IsGeometrySupported(alpha, vertex, resize, normal, out string why);
                        Require(supported, "Known base geometry remains supported before dynamic material indexing");
                        Require(why == null, "Eligible geometry has no rejection reason");
                    }
        foreach (int alpha in new[] {10, 11, 17, 20, 21})
        {
            Require(GlassSceneSupportModel.IsGeometrySupported(alpha, 12, 99, -1, out _), "Additive zero-alpha geometry must not block");
            Require(GlassSceneSupportModel.IsGeometrySupported(alpha, 1, 0, 0, out _), "Dynamic additive uses known base geometry");
            Require(GlassSceneSupportModel.IsGeometrySupported(alpha, 0, 0, 0, out _, 11), "Pixel11 indexing uses known base geometry");
            Require(!GlassSceneSupportModel.IsGeometrySupported(alpha, 1, 99, 0, out _), "Dynamic additive cannot skip unknown base resize");
            Require(!GlassSceneSupportModel.IsGeometrySupported(alpha, 8, 0, 0, out _, 11), "Pixel11 additive cannot skip unknown base vertex mode");
            Require(!GlassSceneSupportModel.IsGeometrySupported(alpha, 0, 0, 7, out _, 11), "Pixel11 additive cannot skip unknown base normal mode");
        }
        Require(GlassSceneSupportModel.IsGeometrySupported(20, 1, 0, 0, out _, 11), "Combined dynamic alpha20 uses verified base geometry");
        Require(GlassSceneSupportModel.IsGeometrySupported(9, 1, 8, 1, out _, 11), "Combined indexing preserves supported base geometry");
        foreach (int alpha in new[] {2, 9, 123})
        {
            Require(!GlassSceneSupportModel.IsGeometrySupported(alpha, 8, 0, 0, out _), "Unknown contributing vertex mode");
            Require(!GlassSceneSupportModel.IsGeometrySupported(alpha, -1, 0, 0, out _), "Negative contributing vertex mode");
            Require(!GlassSceneSupportModel.IsGeometrySupported(alpha, 0, 9, 0, out _), "Unknown contributing resize");
            Require(!GlassSceneSupportModel.IsGeometrySupported(alpha, 0, -1, 0, out _), "Negative contributing resize");
            Require(!GlassSceneSupportModel.IsGeometrySupported(alpha, 0, 0, 2, out _), "Unknown normal recomputation");
        }
        Require(GlassSceneSupportModel.TryGetVertexRange(12, 2, 108, out int first, out int count) && first == 36 && count == 72,
            "Cluster starts are triangle indices and cluster lengths are 36 vertices");
        Require(!GlassSceneSupportModel.TryGetVertexRange(12, 2, 107, out _, out _), "Span must fit every vertex");
        Require(!GlassSceneSupportModel.TryGetVertexRange(int.MaxValue, 1, int.MaxValue, out _, out _), "Start multiplication overflow");
        Require(!GlassSceneSupportModel.TryGetVertexRange(0, int.MaxValue, int.MaxValue, out _, out _), "Count multiplication overflow");
        Require(!GlassSceneSupportModel.TryGetVertexRange(-1, 1, 36, out _, out _), "Negative start");
        Require(!GlassSceneSupportModel.TryGetVertexRange(0, 0, 36, out _, out _), "Empty span is not contributing geometry");
        Console.WriteLine("PASS " + checks + " pure geometry eligibility/range checks; shading-only parameters are intentionally absent.");
    }
}
