using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using UnityEngine;
using VoxelEngine;

namespace GraphicalTweaks
{
    internal static class NativeFeatureContracts
    {
        private static readonly Lazy<Dictionary<string, string>> Results = new Lazy<Dictionary<string, string>>(Inspect);

        internal static void Require(string feature)
        {
            if (!Results.Value.TryGetValue(feature, out string reason))
                throw new NotSupportedException("Missing native feature contract " + feature);
            if (reason != null) throw new NotSupportedException(reason);
        }

        private static Dictionary<string, string> Inspect()
        {
            // Read a private byte copy so the metadata reader holds no game-file handle.
            using (var bytes = new MemoryStream(File.ReadAllBytes(typeof(Brick).Assembly.Location), false))
            using (var module = ModuleDefinition.ReadModule(bytes))
                return NativeContractFingerprint.VerifyFeatures(module, NativeContractCatalog.Features, feature =>
                {
                    if (feature.name == "glass-bootstrap")
                        using (var asset = File.OpenRead(Path.Combine(Application.dataPath, "globalgamemanagers.assets")))
                        {
                            string shader = NativeShaderContract.Fingerprint(asset, "Custom/Voxel");
                            if (shader != "11713caa0df734d035f390e45941a9f4280faf680e33ee88888ada6d385e7ac3" &&
                                shader != "53a8057f80bf08691c4850eccfa5765f2819d492679bd3deefd5d7acd3ca85f8")
                                throw new NotSupportedException("Native transparent shader pass changed.");
                        }
                });
        }
    }
}
