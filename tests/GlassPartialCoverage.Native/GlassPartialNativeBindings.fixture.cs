#if GLASS_PARTIAL_NATIVE_FIXTURE
using UnityEngine;
namespace GraphicalTweaks
{
    // Verbatim DTO from src/GraphicalTweaks/GlassGeometryRemap.cs. Native
    // coverage fixture only; the main already compiles that original source.
    internal readonly struct GlassGeometryRemapBindings
    {
        internal readonly ComputeBuffer ShapeMeshData, TriBrickIDMapping, OriginalMapping, DrawMappingClipZBuffer, ArgsBuffer;
        internal readonly int MappingStartIndex, MappingCount, ArgsOffsetBytes;
        internal GlassGeometryRemapBindings(ComputeBuffer shape, ComputeBuffer mapping, ComputeBuffer originalMapping,
            ComputeBuffer clip, ComputeBuffer args, int start, int count, int argsOffset)
        {
            ShapeMeshData = shape; TriBrickIDMapping = mapping; OriginalMapping = originalMapping;
            DrawMappingClipZBuffer = clip; ArgsBuffer = args;
            MappingStartIndex = start; MappingCount = count; ArgsOffsetBytes = argsOffset;
        }
    }
}
#endif
