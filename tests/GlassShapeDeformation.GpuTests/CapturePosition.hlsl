// Test-only original stream-output adapter. Geometry implementation remains in our shared include.
#include "GlassSurfaceGeometry.hlsl"
struct PositionCapture { float3 position : CAPTURE0; };
[maxvertexcount(1)]
void CapturePosition(point GTSurfaceVaryings input[1], inout PointStream<PositionCapture> output)
{
    PositionCapture value;
    value.position=input[0].rendererPosition;
    output.Append(value);
}
