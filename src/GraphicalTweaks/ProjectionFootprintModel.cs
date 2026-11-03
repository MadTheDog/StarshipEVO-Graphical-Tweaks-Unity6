using System;

namespace GraphicalTweaks
{
    public struct ProjectionVector
    {
        public float X, Y, Z;
        public ProjectionVector(float x, float y, float z) { X = x; Y = y; Z = z; }
    }
    public static class ProjectionFootprintModel
    {
        // Inputs are the center and three half-axis vectors in camera space.
        // Small distant boxes use the perspective derivative; its <=3.2% depth
        // variation is covered by the 4.5px target's margin above the 4px gate.
        // Larger depth spans project every corner, including off-axis depth.
        public static float MinimumPixels(ProjectionVector center, ProjectionVector a, ProjectionVector b, ProjectionVector c, float focalX, float focalY)
        {
            float depth = -center.Z;
            float span = Math.Abs(a.Z) + Math.Abs(b.Z) + Math.Abs(c.Z);
            if (depth <= span + 0.001f) return float.PositiveInfinity;
            if (span <= depth * 0.03125f)
            {
                float x = center.X / depth, y = center.Y / depth;
                float rx = Math.Abs(a.X + x * a.Z) + Math.Abs(b.X + x * b.Z) + Math.Abs(c.X + x * c.Z);
                float ry = Math.Abs(a.Y + y * a.Z) + Math.Abs(b.Y + y * b.Z) + Math.Abs(c.Y + y * c.Z);
                return 2 * Math.Min(rx * focalX, ry * focalY) / depth;
            }
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                float sa = (i & 1) == 0 ? -1 : 1, sb = (i & 2) == 0 ? -1 : 1, sc = (i & 4) == 0 ? -1 : 1;
                float z = -(center.Z + sa * a.Z + sb * b.Z + sc * c.Z);
                float x = (center.X + sa * a.X + sb * b.X + sc * c.X) / z;
                float y = (center.Y + sa * a.Y + sb * b.Y + sc * c.Y) / z;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            return Math.Min((maxX - minX) * focalX, (maxY - minY) * focalY);
        }
    }
}
