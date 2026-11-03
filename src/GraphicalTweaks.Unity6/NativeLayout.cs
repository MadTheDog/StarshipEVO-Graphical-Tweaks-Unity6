using System;
using System.Runtime.InteropServices;
using VoxelEngine;

namespace GraphicalTweaks
{
    internal static class NativeLayout
    {
        internal static void Verify(Action<string> log)
        {
            int shape = Marshal.SizeOf(typeof(BrickMeshResources.ShapeMeshData));
            int build = Marshal.SizeOf(typeof(BrickRenderData));
            int material = Marshal.SizeOf(typeof(VoxelMaterial));
            log("Native layout: shape=" + shape + ", build=" + build + ", material=" + material + ".");
            if (shape != 124 || build != 44 || material != 132)
                throw new NotSupportedException("Native rendering buffer layout differs from the inspected build.");
        }
    }
}
