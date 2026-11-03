using System;
using System.Collections.Generic;

namespace GraphicalTweaks
{
    // Immutable admission snapshot only. The native owner must discard this
    // token when its source/material catalogue generation changes; this class
    // does not watch live engine resources.
    internal sealed class GlassNominalProfileAdmission
    {
        internal int BrickId { get; }
        private GlassNominalProfileAdmission(int brickId) { BrickId = brickId; }

        internal static bool TryAdmit(int sourceType, string sourceSha256, int[][] groups,
            IReadOnlyDictionary<int, GlassShapeDeformationMaterial> materials,
            out GlassNominalProfileAdmission admission)
        {
            admission = null;
            if (!GlassNominalCapProfiles.AdmissionMatches(sourceType, sourceSha256, groups, materials)) return false;
            admission = new GlassNominalProfileAdmission(sourceType);
            return true;
        }
    }

    internal static class GlassNominalCapProfiles
    {
        internal static bool TryAdmit(int sourceType, string sourceSha256, int[][] allGroupTriangles,
            IReadOnlyDictionary<int, GlassShapeDeformationMaterial> usedCapMaterials,
            out GlassNominalProfileAdmission admission) =>
            GlassNominalProfileAdmission.TryAdmit(sourceType, sourceSha256, allGroupTriangles, usedCapMaterials, out admission);

        internal static bool TryBuild(GlassNominalProfileAdmission admission, int countX, int countY, int countZ,
            float unit, GlassShapeRotation rotation, GlassPolygonPoint exactPlacement, out GlassNominalCap[] caps)
        {
            caps = null;
            if (admission == null || countX < 0 || countX > 15 || countY < 0 || countY > 15 || countZ < 0 || countZ > 15 ||
                (unit != .125f && unit != .25f && unit != .5f && unit != 1f) ||
                !Grid(exactPlacement.X) || !Grid(exactPlacement.Y) || !Grid(exactPlacement.Z) ||
                !GlassShapeContactModel.TryCanonicalRotation(rotation, out var canonical)) return false;
            int id = admission.BrickId;
            if (id != 118 && id != 317 && id != 209) return false;
            var result = new GlassNominalCap[id == 209 ? 2 : 3];
            var counts = new[] { countX, countY, countZ };
            var anchor = id == 317 ? new GlassPolygonPoint(-.5, 0, -.5) : new GlassPolygonPoint(-1.5, 0, -.5);
            for (int bit = 0; bit < result.Length; bit++)
            {
                int axis, u, v, family;
                uint compatible;
                if (id == 118)
                { axis = bit; u = bit == 0 ? 1 : 0; v = bit == 2 ? 1 : 2; family = 1; compatible = 7; }
                else if (id == 317)
                { axis = bit == 0 ? 1 : bit == 1 ? 0 : 2; u = bit == 1 ? 1 : 0; v = bit == 2 ? 1 : 2; family = bit == 0 ? 2 : 1; compatible = bit == 0 ? 1u : 6u; }
                else
                { axis = bit == 0 ? 0 : 2; u = bit == 0 ? 1 : 0; v = bit == 0 ? 2 : 1; family = 3; compatible = bit == 0 ? 2u : 1u; }
                var local = Template(family, counts[u], counts[v], countX);
                var boundary = new GlassPolygonPoint[local.Length];
                var rotatedAnchor = Apply(canonical, Scale(anchor, unit));
                // Keep this addition order identical for both nominal ends:
                // rotated grid anchor + exact placement, then rotated controls.
                var worldAnchor = Add(rotatedAnchor, exactPlacement);
                for (int i = 0; i < local.Length; i++)
                {
                    var p = new double[3]; p[u] = local[i].X * unit; p[v] = local[i].Y * unit;
                    boundary[i] = Add(worldAnchor, Apply(canonical, new GlassPolygonPoint(p[0], p[1], p[2])));
                }
                var n = new double[3]; n[axis] = -1;
                var normal = Apply(canonical, new GlassPolygonPoint(n[0], n[1], n[2]));
                double signedArea = OrientedArea(boundary, normal);
                if (signedArea == 0 || double.IsNaN(signedArea) || double.IsInfinity(signedArea)) return false;
                if (signedArea < 0) Array.Reverse(boundary);
                if (!GlassNominalCap.TryCreate(bit, id, family, compatible, normal, boundary, out result[bit])) return false;
            }
            caps = result;
            return true;
        }

        // These are ideal semantic cap controls, not replacement mesh vertices.
        // The frozen source correspondence establishes that inner free-mode
        // controls take a FULL count, despite their ideal coordinate being 1.
        private static GlassPolygonPoint[] Template(int family, int u, int v, int radial)
        {
            if (family == 1) return new[] { P(0, 2 + v), P(0, 1 + v), P(1 + u, 0), P(2 + u, 0) };
            if (family == 2) return new[] { P(0, 1 + v), P(1 + u, 1 + v), P(1 + u, 0), P(2 + u, 0), P(2 + u, 2 + v), P(0, 2 + v) };
            // Shared fixed float samples of the five-segment quarter arc. Avoid
            // runtime trig/platform differences in exact nominal key identity.
            var cosine = new[] { 1f, .95105654f, .809017f, .58778524f, .309017f, 0f };
            var result = new GlassPolygonPoint[12];
            for (int j = 0; j < 6; j++)
            {
                result[j] = P(cosine[5 - j] * (2d + radial), cosine[j] * (2d + radial));
                result[6 + j] = P(cosine[j] * (1.5d + radial), cosine[5 - j] * (1.5d + radial));
            }
            return result;
        }
        private static GlassPolygonPoint P(double x, double y) => new GlassPolygonPoint(x, y, 0);
        private static GlassPolygonPoint Add(GlassPolygonPoint a, GlassPolygonPoint b) => new GlassPolygonPoint(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        private static GlassPolygonPoint Scale(GlassPolygonPoint p, double scale) => new GlassPolygonPoint(p.X * scale, p.Y * scale, p.Z * scale);
        private static GlassPolygonPoint Apply(GlassShapeRotation r, GlassPolygonPoint p) =>
            new GlassPolygonPoint(r.M00 * p.X + r.M01 * p.Y + r.M02 * p.Z,
                r.M10 * p.X + r.M11 * p.Y + r.M12 * p.Z, r.M20 * p.X + r.M21 * p.Y + r.M22 * p.Z);
        private static bool Grid(double value) => !double.IsNaN(value) && !double.IsInfinity(value) &&
            Math.Abs(value) <= 1048576 && value * 32 == Math.Truncate(value * 32);
        private static double OrientedArea(GlassPolygonPoint[] points, GlassPolygonPoint normal)
        {
            double result = 0; var origin = points[0];
            for (int i = 0; i < points.Length; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Length];
                double ax = a.X - origin.X, ay = a.Y - origin.Y, az = a.Z - origin.Z;
                double bx = b.X - origin.X, by = b.Y - origin.Y, bz = b.Z - origin.Z;
                result += (ay * bz - az * by) * normal.X + (az * bx - ax * bz) * normal.Y + (ax * by - ay * bx) * normal.Z;
            }
            return result;
        }

        // The caller supplies a hash of the actual complete native record range,
        // not a name/version assertion. Source bytes and material deformation
        // fields are independent gates; neither alone establishes this policy.
        internal static bool AdmissionMatches(int id, string hash, int[][] groups,
            IReadOnlyDictionary<int, GlassShapeDeformationMaterial> materials)
        {
            string expectedHash;
            int[][] expectedGroups;
            int[] materialIds;
            if (id == 118)
            {
                expectedHash = "06AD79D6C5B28D3151AA37B81CBE8965D5B0B04F7FEB92D67DFA331E941FF6AB";
                expectedGroups = new[] { new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 } };
                materialIds = new[] { 271 };
            }
            else if (id == 317)
            {
                expectedHash = "5625F268A5EDE1F4C0648F77F73B040E207FDD0ED98BE17B109E5242F70D4736";
                expectedGroups = new[] { new[] { 4, 5, 8, 9 }, new[] { 6, 7 }, new[] { 10, 11 } };
                materialIds = new[] { 701 };
            }
            else if (id == 209)
            {
#if SEVO_UNITY6
                // The inspected Unity6 source reorders only non-cap triangles.
                // Complete winding-preserving triangle records are identical as
                // a multiset; all selected cap records keep their original indices.
                expectedHash = "C6F42D354DC0445F8904021393FDDE923C0740B39E82A953C1CC32B61086D9F8";
#else
                expectedHash = "4367C7DE21165DB4395E56C82FEB6A4B805B2D6B9191AA1C95B8D6797AA89D0A";
#endif
                expectedGroups = new[] { Sequence(0, 10), Sequence(10, 10), Sequence(30, 10) };
                materialIds = new[] { 511 };
            }
            else return false;
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                groups == null || groups.Length != expectedGroups.Length || materials == null) return false;
            for (int bit = 0; bit < groups.Length; bit++)
            {
                if (groups[bit] == null || groups[bit].Length != expectedGroups[bit].Length) return false;
                for (int t = 0; t < groups[bit].Length; t++)
                    if (groups[bit][t] != expectedGroups[bit][t]) return false;
            }
            // Pin materials actually referenced by these selected caps. A
            // declared body/unused slot is not a deformation input to this join.
            foreach (int materialId in materialIds)
            {
                if (!materials.TryGetValue(materialId, out var m) || m.ResizeMode != (id == 209 ? 8 : 0) ||
                    m.VertexMode != 0 || m.AlphaMode != 2 || m.ResizeUnit != 1 ||
                    m.GeometryLow != (id == 209 ? 0 : -.001f) || m.GeometryHigh != (id == 209 ? 0 : .001f) ||
                    m.BorderIndex != -1 || m.BuildingOffset != 0) return false;
            }
            return true;
        }
        private static int[] Sequence(int first, int count)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++) result[i] = first + i;
            return result;
        }
    }
}
