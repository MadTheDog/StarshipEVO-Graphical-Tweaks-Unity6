using System;

namespace GraphicalTweaks
{
    internal readonly struct GlassPolygonPoint
    {
        internal readonly double X,Y,Z;
        internal GlassPolygonPoint(double x,double y,double z){X=x;Y=y;Z=z;}
        internal double Axis(int axis)=>axis==0?X:axis==1?Y:Z;
    }
    internal readonly struct GlassPolygonTriangle
    {
        internal readonly GlassPolygonPoint A,B,C;
        internal GlassPolygonTriangle(GlassPolygonPoint a,GlassPolygonPoint b,GlassPolygonPoint c){A=a;B=b;C=c;}
    }
    internal sealed class GlassPolygonFaceGroup
    {
        internal readonly int Bit;
        internal readonly GlassPolygonTriangle[] Triangles;
        internal GlassPolygonFaceGroup(int bit,params GlassPolygonTriangle[] triangles){Bit=bit;Triangles=triangles;}
    }
    internal readonly struct GlassPolygonCoverageBrick
    {
        internal readonly long Entity;
        internal readonly int BuildId;
        internal readonly byte Health;
        internal readonly GlassPolygonFaceGroup[] Groups;
        internal GlassPolygonCoverageBrick(long entity,int buildId,byte health,params GlassPolygonFaceGroup[] groups)
        {Entity=entity;BuildId=buildId;Health=health;Groups=groups;}
    }
    internal readonly struct GlassPolygonCoverageChange
    {
        internal readonly int BuildId;
        internal readonly uint Mask;
        internal GlassPolygonCoverageChange(int buildId,uint mask){BuildId=buildId;Mask=mask;}
    }
    internal sealed class GlassPolygonCoverageLimits
    {
        internal readonly int MaximumCandidates,MaximumFragments,MaximumOperations,MaximumTrianglesPerBrick,
            MaximumCellsPerTriangle,MaximumCellsPerBrick,MaximumTrianglesTotal,MaximumTrianglesPerCell;
        internal readonly double CellSize,CoordinateLimit;
        internal GlassPolygonCoverageLimits(int maximumCandidates=256,int maximumFragments=256,int maximumOperations=8192,
            int maximumTrianglesPerBrick=512,int maximumCellsPerTriangle=256,int maximumCellsPerBrick=8192,
            int maximumTrianglesTotal=262144,double cellSize=8,double coordinateLimit=1048576,int maximumTrianglesPerCell=512)
        {
            if(maximumCandidates<1||maximumFragments<1||maximumOperations<1||maximumTrianglesPerBrick<1||
               maximumCellsPerTriangle<1||maximumCellsPerBrick<1||maximumTrianglesTotal<1||maximumTrianglesPerCell<1||
               double.IsNaN(cellSize)||double.IsInfinity(cellSize)||cellSize<=0||
               double.IsNaN(coordinateLimit)||double.IsInfinity(coordinateLimit)||coordinateLimit<=0||coordinateLimit/cellSize>1000000000)
                throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
            MaximumCandidates=maximumCandidates;MaximumFragments=maximumFragments;MaximumOperations=maximumOperations;
            MaximumTrianglesPerBrick=maximumTrianglesPerBrick;MaximumCellsPerTriangle=maximumCellsPerTriangle;
            MaximumCellsPerBrick=maximumCellsPerBrick;MaximumTrianglesTotal=maximumTrianglesTotal;
            MaximumTrianglesPerCell=maximumTrianglesPerCell;
            CellSize=cellSize;CoordinateLimit=coordinateLimit;
        }
    }
}
