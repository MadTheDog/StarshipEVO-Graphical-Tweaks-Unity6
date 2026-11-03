using System;
using System.Collections.Generic;

namespace GraphicalTweaks
{
    internal readonly struct GlassFaceCoverageBox
    {
        internal readonly long Entity, MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        internal readonly int BuildId;
        internal readonly byte Health;
        internal GlassFaceCoverageBox(long entity,int id,long minX,long minY,long minZ,long maxX,long maxY,long maxZ,byte health=255)
        { Entity=entity;BuildId=id;MinX=minX;MinY=minY;MinZ=minZ;MaxX=maxX;MaxY=maxY;MaxZ=maxZ;Health=health; }
    }
    internal readonly struct GlassFaceCoverageChange
    {
        internal readonly int BuildId;
        internal readonly uint Mask;
        internal GlassFaceCoverageChange(int id,uint mask) { BuildId=id;Mask=mask; }
    }
    internal sealed class GlassFaceCoverageModel
    {
        // Coordinates are exact entity-local 1/32 units. Bits are +X,-X,+Y,-Y,+Z,-Z.
        private const long TileSize = 256;
        private const long CoordinateLimit = 1L << 40;
        private const long MaximumCellsPerFace = 4096;
        private readonly Dictionary<int, Face[]> boxes = new Dictionary<int, Face[]>();
        private readonly Dictionary<Cell, HashSet<Face>> cells = new Dictionary<Cell, HashSet<Face>>();
        private readonly Dictionary<int, uint> masks = new Dictionary<int, uint>();
        private readonly Dictionary<int, uint> changes = new Dictionary<int, uint>();
        internal long CandidateChecks {get; private set;}
        internal void Upsert(GlassFaceCoverageBox box)
        {
            var affected = new HashSet<int>();
            RemoveIndexed(box.BuildId, affected);
            if (box.BuildId >= 0 && Valid(box))
            {
                var faces = new Face[6];
                long[] min = {box.MinX,box.MinY,box.MinZ}, max = {box.MaxX,box.MaxY,box.MaxZ};
                for(int bit=0;bit<6;bit++)
                {
                    int axis=bit/2, u=(axis+1)%3, v=(axis+2)%3;
                    var face = new Face(box.Entity,box.BuildId,bit,bit%2==0?max[axis]:min[axis],min[u],min[v],max[u],max[v]);
                    faces[bit]=face;
                    if (!face.Indexable) continue;
                    foreach(Cell cell in face.Cells())
                    {
                        if(!cells.TryGetValue(cell,out var bucket)) cells.Add(cell,bucket=new HashSet<Face>());
                        bucket.Add(face);
                    }
                    Affect(face,affected);
                }
                boxes.Add(box.BuildId,faces);
                affected.Add(box.BuildId);
            }
            Recompute(affected);
        }
        internal void Remove(int id)
        {
            var affected = new HashSet<int>(); RemoveIndexed(id,affected); Recompute(affected);
        }
        internal uint GetMask(int id) => masks.TryGetValue(id,out uint value)?value:0;
        internal void Clear()
        {
            foreach(var pair in masks) if(pair.Value!=0) changes[pair.Key]=0;
            cells.Clear();boxes.Clear();masks.Clear();CandidateChecks=0;
        }
        internal void DrainChanges(List<GlassFaceCoverageChange> output)
        {
            foreach(var pair in changes) output.Add(new GlassFaceCoverageChange(pair.Key,pair.Value));
            changes.Clear();
        }
        private static bool Valid(GlassFaceCoverageBox b)
            => b.Health==byte.MaxValue && b.MinX<b.MaxX && b.MinY<b.MaxY && b.MinZ<b.MaxZ &&
               b.MinX>=-CoordinateLimit && b.MinY>=-CoordinateLimit && b.MinZ>=-CoordinateLimit &&
               b.MaxX<=CoordinateLimit && b.MaxY<=CoordinateLimit && b.MaxZ<=CoordinateLimit;
        private void SetMask(int id,uint value)
        {
            if(GetMask(id)!=value) changes[id]=value;
            if(value==0) masks.Remove(id);else masks[id]=value;
        }
        private void RemoveIndexed(int id,HashSet<int> affected)
        {
            if(!boxes.TryGetValue(id,out var faces)) return;
            foreach(var face in faces)
            {
                if(!face.Indexable)continue;
                Affect(face,affected);
                foreach(Cell cell in face.Cells())
                {
                    var bucket=cells[cell];bucket.Remove(face);if(bucket.Count==0)cells.Remove(cell);
                }
            }
            boxes.Remove(id);SetMask(id,0);affected.Remove(id);
        }
        private HashSet<Face> Nearby(Face face)
        {
            var result=new HashSet<Face>();
            if(face.Indexable) foreach(Cell cell in face.Cells()) if(cells.TryGetValue(cell,out var bucket))
                foreach(var other in bucket) result.Add(other);
            return result;
        }
        private void Affect(Face face,HashSet<int> affected)
        {
            foreach(var other in Nearby(face)) if(face.Overlaps(other)) affected.Add(other.Id);
        }
        private void Recompute(HashSet<int> affected)
        {
            foreach(int id in affected)
            {
                if(!boxes.TryGetValue(id,out var faces))continue;
                uint mask=0;
                foreach(var face in faces) if(Covered(face)) mask |= 1u<<face.Bit;
                SetMask(id,mask);
            }
        }
        private bool Covered(Face face)
        {
            if(!face.Indexable)return false;
            var rectangles=new List<Face>();var boundaries=new List<long>{face.U0,face.U1};
            foreach(var other in Nearby(face))
            {
                CandidateChecks++;
                if(other.Id==face.Id || other.Bit!=(face.Bit^1) || !face.Overlaps(other))continue;
                rectangles.Add(other);boundaries.Add(Math.Max(face.U0,other.U0));boundaries.Add(Math.Min(face.U1,other.U1));
            }
            if(rectangles.Count==0)return false;
            boundaries.Sort();
            // Sweep strips at exact rectangle edges, then union V intervals in each strip.
            var intervals=new List<Face>();
            for(int i=1;i<boundaries.Count;i++)
            {
                long left=boundaries[i-1],right=boundaries[i];if(left==right)continue;
                intervals.Clear();foreach(var rectangle in rectangles) if(rectangle.U0<=left && rectangle.U1>=right)intervals.Add(rectangle);
                intervals.Sort((a,b)=>a.V0.CompareTo(b.V0));
                long end=face.V0;
                foreach(var interval in intervals)
                {
                    if(interval.V0>end)break;
                    end=Math.Max(end,interval.V1);if(end>=face.V1)break;
                }
                if(end<face.V1)return false;
            }
            return true;
        }
        private static long Tile(long value) => value>=0?value/TileSize:(value+1)/TileSize-1;
        private sealed class Face
        {
            internal readonly long Entity,Plane,U0,V0,U1,V1;
            internal readonly int Id,Bit;
            internal readonly bool Indexable;
            internal Face(long entity,int id,int bit,long plane,long u0,long v0,long u1,long v1)
            {
                Entity=entity;Id=id;Bit=bit;Plane=plane;U0=u0;V0=v0;U1=u1;V1=v1;
                long width=Tile(u1-1)-Tile(u0)+1,height=Tile(v1-1)-Tile(v0)+1;
                Indexable=width<=MaximumCellsPerFace && height<=MaximumCellsPerFace && width*height<=MaximumCellsPerFace;
            }
            internal bool Overlaps(Face b) => U0<b.U1 && U1>b.U0 && V0<b.V1 && V1>b.V0;
            internal IEnumerable<Cell> Cells()
            {
                for(long u=Tile(U0);u<=Tile(U1-1);u++) for(long v=Tile(V0);v<=Tile(V1-1);v++)
                    yield return new Cell(Entity,Bit/2,Plane,u,v);
            }
        }
        private readonly struct Cell : IEquatable<Cell>
        {
            private readonly long entity,plane,u,v;private readonly int axis;
            internal Cell(long entity,int axis,long plane,long u,long v) {this.entity=entity;this.axis=axis;this.plane=plane;this.u=u;this.v=v;}
            public bool Equals(Cell b)=>entity==b.entity && axis==b.axis && plane==b.plane && u==b.u && v==b.v;
            public override bool Equals(object obj)=>obj is Cell b && Equals(b);
            public override int GetHashCode() {unchecked{int h=entity.GetHashCode();h=h*397^axis;h=h*397^plane.GetHashCode();h=h*397^u.GetHashCode();return h*397^v.GetHashCode();}}
        }
    }
}
