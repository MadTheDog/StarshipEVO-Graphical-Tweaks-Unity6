namespace GraphicalTweaks
{
    // Geometry-only eligibility. Unknown shading is handled by a conservative
    // coverage layer in the shader; it must not disable otherwise valid glass.
    internal static class GlassSceneSupportModel
    {
        internal static bool IsGeometrySupported(int alpha, int vertex, int resize, int normalMode,
                                                 out string reason, int pixelMode = 0)
        {
            bool dynamicIndex = vertex == 1 || pixelMode == 11;
            if (!dynamicIndex && (alpha == 10 || alpha == 11 || alpha == 17 || alpha == 20 || alpha == 21))
            {
                reason = null;
                return true; // Fixed additive alpha-zero families cannot retain loss behind them.
            }
            // Native VS49 deforms using BASE geometry before either index shift.
            // A dynamic additive base can resolve to a contributing material, so
            // it must satisfy the same known-base geometry rules as ordinary glass.
            if (vertex < 0 || vertex > 7) { reason = "vertex mode " + vertex; return false; }
            if (resize < 0 || resize > 8) { reason = "resize mode " + resize; return false; }
            if (normalMode < 0 || normalMode > 1) { reason = "normal mode " + normalMode; return false; }
            reason = null;
            return true;
        }

        internal static bool TryGetVertexRange(int clusterStart, int clusterCount, int available,
                                               out int first, out int count)
        {
            first = count = 0;
            if (clusterStart < 0 || clusterCount <= 0 || available < 0) return false;
            long start = (long)clusterStart * 3;
            long length = (long)clusterCount * 36;
            if (start > int.MaxValue || length > int.MaxValue || start + length > available) return false;
            first = (int)start; count = (int)length;
            return true;
        }
    }
}
