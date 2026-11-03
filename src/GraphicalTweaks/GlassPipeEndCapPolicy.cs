#if SEVO_UNITY6
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace GraphicalTweaks
{
    internal sealed class GlassPipeEndCapAdmission
    {
        internal readonly GlassShapeContactCanonicalizer Policy;
        internal readonly GlassShapeGrouping Grouping;
        internal readonly GlassShapeDeformationMaterial[] Materials;
        internal GlassPipeEndCapAdmission(GlassShapeContactCanonicalizer policy, GlassShapeGrouping grouping, GlassShapeDeformationMaterial[] materials)
        { Policy = policy; Grouping = grouping; Materials = materials; }
    }

    internal static class GlassPipeEndCapPolicy
    {
        internal const ushort BrickId = 195;
        private const string SourceHash = "A46CBF45FD2BB591603B8CF191550A8E7B3BE33F2F6AE9576D64F12FD51061CD";
        private const string SideMaterialHash = "F4B105331975BCC99AA715E853E4EE126CFBD403856FA709349CC189CE848E7D";
        private const string CapMaterialHash = "3BC425EB0135BB7A8BF5EE830143E97203CF7B5D4B5B2C99243C40E0EA7E9C12";

        // Current healthy source only. Native border modes3/5 adjust UV/shading,
        // not vertex positions. This proxy is used solely for contact geometry:
        // native records/materials are retained verbatim by the render remap.
        internal static bool TryAdmit(byte[] records, IReadOnlyDictionary<int, byte[]> materials, out GlassPipeEndCapAdmission admission)
        {
            admission = null;
            if (!Matches(records, 144 * 124, SourceHash) || materials == null || materials.Count != 2 ||
                !materials.TryGetValue(488, out var side) || !Matches(side, 132, SideMaterialHash) ||
                !materials.TryGetValue(489, out var cap) || !Matches(cap, 132, CapMaterialHash)) return false;
            var positions = new GlassShapeFloat3[144];
            for (int i = 0; i < positions.Length; i++)
            {
                int at = i * 124;
                positions[i] = new GlassShapeFloat3(BitConverter.ToSingle(records, at), BitConverter.ToSingle(records, at + 4), BitConverter.ToSingle(records, at + 8));
            }
            if (!GlassShapeContactCanonicalizer.TryCreate(positions, out var policy) ||
                !GlassShapeGrouping.TryCreate(positions, 48, 48, true, policy, out var grouped, out _) || grouped.GroupCount != 2) return false;
            for (int t = 0; t < 48; t++)
                if (grouped.TriangleFaceBits[t] != (t < 24 ? 0u : t < 36 ? 1u : 2u)) return false;
            for (int bit = 0; bit < 2; bit++)
            {
                if (grouped.GroupTriangles[bit].Length != 12) return false;
                for (int n = 0; n < 12; n++) if (grouped.GroupTriangles[bit][n] != 24 + 12 * bit + n) return false;
            }
            var proxies = new GlassShapeDeformationMaterial[48];
            for (int t = 0; t < proxies.Length; t++) proxies[t] = new GlassShapeDeformationMaterial(0, 0, 2, 1f, -.001f, .001f);
            admission = new GlassPipeEndCapAdmission(policy, grouped, proxies); return true;
        }
        private static bool Matches(byte[] bytes, int length, string expected)
        {
            if (bytes == null || bytes.Length != length) return false;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "") == expected;
        }
    }
}
#endif
