using System;
using System.Collections.Generic;
using System.Numerics;

namespace GraphicalTweaks
{
    internal sealed partial class GlassPolygonCoverageModel
    {
        // Inputs are immutable snapshots of actual entity-local triangles. No
        // tolerance merges distinct planes or discards a nonzero uncovered area.
        // This service is called on geometry/lifecycle changes, never per frame.
        private readonly GlassPolygonCoverageLimits limits;
        private readonly Dictionary<int,BrickState> bricks=new Dictionary<int,BrickState>();
        private readonly Dictionary<Cell,HashSet<Surface>> cells=new Dictionary<Cell,HashSet<Surface>>();
        private readonly Dictionary<int,uint> masks=new Dictionary<int,uint>(),changes=new Dictionary<int,uint>();
        private int triangleCount;
        internal GlassPolygonCoverageModel(GlassPolygonCoverageLimits limits=null){this.limits=limits??new GlassPolygonCoverageLimits();}
        internal long CandidateChecks {get;private set;}
        internal long BudgetFailures {get;private set;}
        internal void Upsert(GlassPolygonCoverageBrick brick)
        {
            var affected=new HashSet<int>();RemoveIndexed(brick.BuildId,affected);
            BrickState state=Build(brick);
            if(state!=null)
            {
                bricks.Add(brick.BuildId,state);triangleCount+=state.Triangles.Count;
                foreach(var face in state.Triangles)foreach(var cell in face.Cells)
                {
                    if(!cells.TryGetValue(cell,out var bucket))cells.Add(cell,bucket=new HashSet<Surface>());
                    bucket.Add(face);
                }
                foreach(var face in state.Triangles)Affect(face,affected);
                affected.Add(brick.BuildId);
            }
            Recompute(affected);
        }
        internal void Remove(int id){var affected=new HashSet<int>();RemoveIndexed(id,affected);Recompute(affected);}
        internal void RemoveEntity(long entity)
        {
            var ids=new List<int>();foreach(var pair in bricks)if(pair.Value.Entity==entity)ids.Add(pair.Key);
            var affected=new HashSet<int>();foreach(int id in ids)RemoveIndexed(id,affected);Recompute(affected);
        }
        internal uint GetMask(int id)=>masks.TryGetValue(id,out uint mask)?mask:0;
        internal void Clear()
        {
            foreach(var pair in masks)changes[pair.Key]=0;
            bricks.Clear();cells.Clear();masks.Clear();triangleCount=0;CandidateChecks=BudgetFailures=0;
        }
        internal void DrainChanges(List<GlassPolygonCoverageChange> output)
        {
            if(output==null)throw new ArgumentNullException(nameof(output));
            foreach(var pair in changes)output.Add(new GlassPolygonCoverageChange(pair.Key,pair.Value));changes.Clear();
        }
        private BrickState Build(GlassPolygonCoverageBrick brick)
        {
            if(brick.BuildId<0||brick.Health!=255||brick.Groups==null||brick.Groups.Length==0||brick.Groups.Length>8)return null;
            try
            {
                var state=new BrickState(brick.Entity);uint bits=0;int inputCount=0,cellCount=0;
                var additions=new Dictionary<Cell,int>();
                foreach(var group in brick.Groups)
                {
                    if(group==null||group.Bit<0||group.Bit>7||group.Triangles==null||(bits&(1u<<group.Bit))!=0)return null;
                    bits|=1u<<group.Bit;
                    inputCount=checked(inputCount+group.Triangles.Length);
                    if(inputCount>limits.MaximumTrianglesPerBrick)throw new WorkLimit();
                    var faces=new List<Surface>();state.Groups.Add(group.Bit,faces);
                    foreach(var triangle in group.Triangles)
                    {
                        if(!Valid(triangle.A)||!Valid(triangle.B)||!Valid(triangle.C))return null;
                        Surface face=Surface.Create(brick.Entity,brick.BuildId,state.Triangles.Count,triangle,limits);
                        if(face==null)continue; // Exactly degenerate, including native zero padding.
                        cellCount=checked(cellCount+face.Cells.Length);
                        if(cellCount>limits.MaximumCellsPerBrick||state.Triangles.Count+triangleCount>=limits.MaximumTrianglesTotal)throw new WorkLimit();
                        foreach(var cell in face.Cells)
                        {
                            additions.TryGetValue(cell,out int n);additions[cell]=++n;
                            int existing=cells.TryGetValue(cell,out var bucket)?bucket.Count:0;
                            if(n+existing>limits.MaximumTrianglesPerCell)throw new WorkLimit();
                        }
                        faces.Add(face);state.Triangles.Add(face);
                    }
                }
                return state;
            }
            catch(WorkLimit){BudgetFailures++;return null;}
            catch(OverflowException){BudgetFailures++;return null;}
        }
        private bool Valid(GlassPolygonPoint p)=>Valid(p.X)&&Valid(p.Y)&&Valid(p.Z);
        private bool Valid(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value)&&Math.Abs(value)<=limits.CoordinateLimit;
        private void SetMask(int id,uint mask)
        {if(GetMask(id)!=mask)changes[id]=mask;if(mask==0)masks.Remove(id);else masks[id]=mask;}
        private void RemoveIndexed(int id,HashSet<int> affected)
        {
            if(!bricks.TryGetValue(id,out var state))return;
            foreach(var face in state.Triangles)Affect(face,affected);
            foreach(var face in state.Triangles)foreach(var cell in face.Cells)
            {var bucket=cells[cell];bucket.Remove(face);if(bucket.Count==0)cells.Remove(cell);}
            triangleCount-=state.Triangles.Count;bricks.Remove(id);SetMask(id,0);affected.Remove(id);
        }
        private HashSet<Surface> Nearby(Surface face)
        {
            var found=new HashSet<Surface>();foreach(var cell in face.Cells)if(cells.TryGetValue(cell,out var bucket))
                foreach(var other in bucket)found.Add(other);
            return found;
        }
        private void Affect(Surface face,HashSet<int> affected)
        {foreach(var other in Nearby(face))if(other.Id!=face.Id&&other.Winding!=face.Winding&&face.Overlaps(other))affected.Add(other.Id);}
        private void Recompute(HashSet<int> affected)
        {
            foreach(int id in affected)
            {
                if(!bricks.TryGetValue(id,out var state))continue;uint mask=0;
                foreach(var group in state.Groups)
                {
                    bool covered=group.Value.Count>0;
                    var budget=new Budget(limits.MaximumOperations);
                    foreach(var face in group.Value)if(!Covered(face,budget)){covered=false;break;}
                    if(covered)mask|=1u<<group.Key;
                }
                SetMask(id,mask);
            }
        }
        private bool Covered(Surface face,Budget budget)
        {
            try
            {
                var candidates=new List<Surface>();
                foreach(var other in Nearby(face))
                {
                    CandidateChecks++;
                    budget.Step();
                    if(other.Id==face.Id||other.Winding==face.Winding||!face.Overlaps(other))continue;
                    // An identical opposite triangle is a complete exact proof,
                    // avoiding subtraction allocations for common cube joins.
                    if(SameTriangle(face.Points,other.Points))return true;
                    candidates.Add(other);if(candidates.Count>limits.MaximumCandidates)throw new WorkLimit();
                }
                if(candidates.Count==0)return false;
                candidates.Sort((a,b)=>a.Id!=b.Id?a.Id.CompareTo(b.Id):a.Order.CompareTo(b.Order));
                var fragments=new List<List<Point2>>{new List<Point2>(face.Points)};
                foreach(var other in candidates)
                {
                    var remainder=new List<List<Point2>>();
                    foreach(var fragment in fragments)
                    {
                        var inside=fragment;
                        for(int edge=0;edge<3&&inside.Count>=3;edge++)
                        {
                            Split(inside,other.Points[edge],other.Points[(edge+1)%3],other.Winding,budget,out var next,out var outside);
                            if(HasArea(outside,budget))
                            {remainder.Add(outside);if(remainder.Count>limits.MaximumFragments)throw new WorkLimit();}
                            inside=next;
                        }
                    }
                    fragments=remainder;if(fragments.Count==0)return true;
                }
                return false;
            }
            catch(WorkLimit){BudgetFailures++;return false;}
        }
        private static bool SameTriangle(Point2[] a,Point2[] b)
        {
            foreach(var point in a)if(!point.Equals(b[0])&&!point.Equals(b[1])&&!point.Equals(b[2]))return false;
            return true;
        }
        private static void Split(List<Point2> polygon,Point2 a,Point2 b,int winding,Budget budget,
                                  out List<Point2> inside,out List<Point2> outside)
        {
            inside=new List<Point2>();outside=new List<Point2>();
            Point2 previous=polygon[polygon.Count-1];Number pd=Cross(a,b,previous,budget);int ps=pd.Sign*winding;
            foreach(var point in polygon)
            {
                Number d=Cross(a,b,point,budget);int s=d.Sign*winding;
                if((ps<0&&s>0)||(ps>0&&s<0))
                {
                    Number t=pd/(pd-d);Point2 crossing=previous+(point-previous)*t;
                    Add(inside,crossing);Add(outside,crossing);
                }
                if(s>=0)Add(inside,point);if(s<=0)Add(outside,point);
                previous=point;pd=d;ps=s;
            }
            Close(inside);Close(outside);
            if(inside.Count>32||outside.Count>32)throw new WorkLimit();
        }
        private static void Add(List<Point2> points,Point2 point){if(points.Count==0||!points[points.Count-1].Equals(point))points.Add(point);}
        private static void Close(List<Point2> points){if(points.Count>1&&points[0].Equals(points[points.Count-1]))points.RemoveAt(points.Count-1);}
        private static bool HasArea(List<Point2> polygon,Budget budget)
        {
            if(polygon.Count<3)return false;Number area=Number.Zero;
            for(int i=1;i<polygon.Count-1;i++)area+=Cross(polygon[0],polygon[i],polygon[i+1],budget);
            return area.Sign!=0;
        }
        private static Number Cross(Point2 a,Point2 b,Point2 c,Budget budget)
        {budget.Step();return (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);}
        private sealed class WorkLimit:Exception{}
        private sealed class Budget
        {
            private int remaining;
            internal Budget(int remaining){this.remaining=remaining;}
            internal void Step(){if(--remaining<0)throw new WorkLimit();}
        }
        private sealed class BrickState
        {
            internal readonly long Entity;
            internal readonly List<Surface> Triangles=new List<Surface>();
            internal readonly Dictionary<int,List<Surface>> Groups=new Dictionary<int,List<Surface>>();
            internal BrickState(long entity){Entity=entity;}
        }
        private sealed class Surface
        {
            internal int Id,Order,Winding;
            internal double MinU,MinV,MaxU,MaxV;
            internal Point2[] Points;
            internal Cell[] Cells;
            internal bool Overlaps(Surface other)=>MinU<other.MaxU&&other.MinU<MaxU&&MinV<other.MaxV&&other.MinV<MaxV;
            internal static Surface Create(long entity,int id,int order,GlassPolygonTriangle triangle,GlassPolygonCoverageLimits limits)
            {
                var p=new[]{triangle.A,triangle.B,triangle.C};var exact=new Number[3,3];
                for(int v=0;v<3;v++)for(int axis=0;axis<3;axis++)exact[v,axis]=Number.FromDouble(p[v].Axis(axis));
                Number nx=(exact[1,1]-exact[0,1])*(exact[2,2]-exact[0,2])-(exact[1,2]-exact[0,2])*(exact[2,1]-exact[0,1]);
                Number ny=(exact[1,2]-exact[0,2])*(exact[2,0]-exact[0,0])-(exact[1,0]-exact[0,0])*(exact[2,2]-exact[0,2]);
                Number nz=(exact[1,0]-exact[0,0])*(exact[2,1]-exact[0,1])-(exact[1,1]-exact[0,1])*(exact[2,0]-exact[0,0]);
                if(nx.Sign==0&&ny.Sign==0&&nz.Sign==0)return null;
                var plane=new Plane(nx,ny,nz,-(nx*exact[0,0]+ny*exact[0,1]+nz*exact[0,2]));
                int u=(plane.Drop+1)%3,vAxis=(plane.Drop+2)%3;var points=new Point2[3];
                double minU=double.PositiveInfinity,minV=minU,maxU=double.NegativeInfinity,maxV=maxU;
                for(int i=0;i<3;i++)
                {
                    points[i]=new Point2(exact[i,u],exact[i,vAxis]);
                    minU=Math.Min(minU,p[i].Axis(u));maxU=Math.Max(maxU,p[i].Axis(u));
                    minV=Math.Min(minV,p[i].Axis(vAxis));maxV=Math.Max(maxV,p[i].Axis(vAxis));
                }
                int winding=Cross(points[0],points[1],points[2],new Budget(1)).Sign;
                long x0=(long)Math.Floor(minU/limits.CellSize),x1=(long)Math.Floor(maxU/limits.CellSize);
                long y0=(long)Math.Floor(minV/limits.CellSize),y1=(long)Math.Floor(maxV/limits.CellSize);
                long width=x1-x0+1,height=y1-y0+1;
                if(width>limits.MaximumCellsPerTriangle||height>limits.MaximumCellsPerTriangle||width*height>limits.MaximumCellsPerTriangle)throw new WorkLimit();
                var cells=new Cell[(int)(width*height)];int index=0;
                for(long x=x0;x<=x1;x++)for(long y=y0;y<=y1;y++)cells[index++]=new Cell(entity,plane,x,y);
                return new Surface{Id=id,Order=order,Winding=winding,Points=points,Cells=cells,MinU=minU,MaxU=maxU,MinV=minV,MaxV=maxV};
            }
        }
        private sealed class Plane:IEquatable<Plane>
        {
            // A common denominator plus GCD makes the exact dyadic plane
            // independent of triangle size, tessellation and winding.
            private readonly BigInteger a,b,c,d;private readonly int hash;
            internal readonly int Drop;
            internal Plane(Number x,Number y,Number z,Number w)
            {
                Number[] values={x,y,z,w};BigInteger denominator=BigInteger.One;
                foreach(var value in values)denominator=denominator/BigInteger.GreatestCommonDivisor(denominator,value.D)*value.D;
                var n=new BigInteger[4];BigInteger divisor=BigInteger.Zero;
                for(int i=0;i<4;i++){n[i]=values[i].N*(denominator/values[i].D);divisor=BigInteger.GreatestCommonDivisor(divisor,BigInteger.Abs(n[i]));}
                int sign=n[0].Sign!=0?n[0].Sign:n[1].Sign!=0?n[1].Sign:n[2].Sign;
                for(int i=0;i<4;i++){n[i]=n[i]/divisor*sign;if(!(BigInteger.Abs(n[i])>>2048).IsZero)throw new WorkLimit();}
                a=n[0];b=n[1];c=n[2];d=n[3];
                Drop=BigInteger.Abs(a)>=BigInteger.Abs(b)?0:1;
                if(BigInteger.Abs(c)>BigInteger.Abs(n[Drop]))Drop=2;
                hash=unchecked(((a.GetHashCode()*397^b.GetHashCode())*397^c.GetHashCode())*397^d.GetHashCode());
            }
            public bool Equals(Plane other)=>other!=null&&a==other.a&&b==other.b&&c==other.c&&d==other.d;
            public override bool Equals(object other)=>other is Plane plane&&Equals(plane);
            public override int GetHashCode()=>hash;
        }
        private readonly struct Cell:IEquatable<Cell>
        {
            private readonly long entity,x,y;private readonly Plane plane;
            internal Cell(long entity,Plane plane,long x,long y){this.entity=entity;this.plane=plane;this.x=x;this.y=y;}
            public bool Equals(Cell other)=>entity==other.entity&&x==other.x&&y==other.y&&plane.Equals(other.plane);
            public override bool Equals(object other)=>other is Cell cell&&Equals(cell);
            public override int GetHashCode()=>unchecked(((entity.GetHashCode()*397^plane.GetHashCode())*397^x.GetHashCode())*397^y.GetHashCode());
        }
        private readonly struct Point2:IEquatable<Point2>
        {
            internal readonly Number X,Y;
            internal Point2(Number x,Number y){X=x;Y=y;}
            public bool Equals(Point2 other)=>X.Equals(other.X)&&Y.Equals(other.Y);
            public static Point2 operator +(Point2 a,Point2 b)=>new Point2(a.X+b.X,a.Y+b.Y);
            public static Point2 operator -(Point2 a,Point2 b)=>new Point2(a.X-b.X,a.Y-b.Y);
            public static Point2 operator *(Point2 a,Number b)=>new Point2(a.X*b,a.Y*b);
        }
        private readonly struct Number:IEquatable<Number>
        {
            // Precision exhaustion is an explicit visible fallback, never an
            // approximate zero/area comparison. Intermediate products are
            // reduced before enforcing the stored precision bound.
            internal readonly BigInteger N,D;
            internal static readonly Number Zero=new Number(BigInteger.Zero,BigInteger.One);
            internal int Sign=>N.Sign;
            private Number(BigInteger numerator,BigInteger denominator)
            {
                if(denominator.IsZero)throw new WorkLimit();
                if(numerator.IsZero){N=BigInteger.Zero;D=BigInteger.One;return;}
                if(denominator.Sign<0){numerator=-numerator;denominator=-denominator;}
                BigInteger divisor=BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator),denominator);
                N=numerator/divisor;D=denominator/divisor;
                if(!(BigInteger.Abs(N)>>512).IsZero||!(D>>512).IsZero)throw new WorkLimit();
            }
            internal static Number FromDouble(double value)
            {
                ulong bits=unchecked((ulong)BitConverter.DoubleToInt64Bits(value)),fraction=bits&0xfffffffffffffUL;
                int exponent=(int)((bits>>52)&2047);
                if(exponent==0&&fraction==0)return Zero;
                ulong mantissa=exponent==0?fraction:fraction|(1UL<<52);int shift=exponent==0?-1074:exponent-1023-52;
                while((mantissa&1)==0){mantissa>>=1;shift++;}
                BigInteger numerator=new BigInteger(mantissa);if((bits>>63)!=0)numerator=-numerator;
                return shift>=0?new Number(numerator<<shift,BigInteger.One):new Number(numerator,BigInteger.One<<-shift);
            }
            public bool Equals(Number other)=>N==other.N&&D==other.D;
            public static Number operator +(Number a,Number b)=>a.D==b.D?new Number(a.N+b.N,a.D):new Number(a.N*b.D+b.N*a.D,a.D*b.D);
            public static Number operator -(Number a,Number b)=>a.D==b.D?new Number(a.N-b.N,a.D):new Number(a.N*b.D-b.N*a.D,a.D*b.D);
            public static Number operator -(Number a)=>new Number(-a.N,a.D);
            public static Number operator *(Number a,Number b)=>new Number(a.N*b.N,a.D*b.D);
            public static Number operator /(Number a,Number b)=>new Number(a.N*b.D,a.D*b.N);
        }
    }
}
