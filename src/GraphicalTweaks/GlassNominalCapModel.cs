using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;

namespace GraphicalTweaks
{
    // A nominal cap is an explicit appearance policy, not an exact native
    // occluder. Only a separately audited factory may supply these snapshots.
    // Boundary identity uses exact immutable edges, never an area/epsilon test.
    internal sealed class GlassNominalCap
    {
        internal const double CoordinateLimit=1048576;
        internal readonly int Bit,SourceType,ProfileFamily;
        internal readonly uint CompatibleGroupBits;
        internal readonly GlassPolygonPoint Normal;
        internal readonly int NormalAxis,NormalSign;
        internal readonly BoundaryKey Identity;
        private readonly ReadOnlyCollection<GlassPolygonPoint> boundary;
        internal IReadOnlyList<GlassPolygonPoint> Boundary=>boundary;
        // Canonical edge scalar payload only. Object/array/dictionary overhead
        // and the read-only boundary view are not claimed as measured heap use.
        internal long KeyPayloadBytes=>Identity.Count*48L;
        private GlassNominalCap(int bit,int type,int family,uint compatible,GlassPolygonPoint normal,
            int axis,int sign,GlassPolygonPoint[] points,Edge[] edges)
        {
            Bit=bit;SourceType=type;ProfileFamily=family;CompatibleGroupBits=compatible;
            Normal=normal;NormalAxis=axis;NormalSign=sign;Identity=new BoundaryKey(edges);
            boundary=Array.AsReadOnly(points);
        }
        internal static bool TryCreate(int bit,int sourceType,int profileFamily,uint compatibleGroupBits,
            GlassPolygonPoint normal,GlassPolygonPoint[] orderedBoundary,out GlassNominalCap cap)
        {
            cap=null;
            if(bit<0||bit>7||sourceType<0||profileFamily<0||compatibleGroupBits==0||compatibleGroupBits>255||
                orderedBoundary==null||orderedBoundary.Length<3||orderedBoundary.Length>16)return false;
            int axis=-1,sign=0;
            for(int i=0;i<3;i++)
            {
                double value=normal.Axis(i);
                if(value==0)continue;
                if(axis!=-1||(value!=1&&value!=-1))return false;
                axis=i;sign=value==1?1:-1;
            }
            if(axis<0)return false;
            var points=new GlassPolygonPoint[orderedBoundary.Length];
            for(int i=0;i<points.Length;i++)
            {
                var p=orderedBoundary[i];
                if(!Valid(p.X)||!Valid(p.Y)||!Valid(p.Z)||p.Axis(axis)!=orderedBoundary[0].Axis(axis))return false;
                points[i]=new GlassPolygonPoint(Zero(p.X),Zero(p.Y),Zero(p.Z));
                for(int j=0;j<i;j++)if(Equal(points[i],points[j]))return false;
            }
            if(!SimpleNonzeroPolygon(points,axis))return false;
            var edges=new Edge[points.Length];
            for(int i=0;i<edges.Length;i++)edges[i]=new Edge(points[i],points[(i+1)%points.Length]);
            Array.Sort(edges);
            cap=new GlassNominalCap(bit,sourceType,profileFamily,compatibleGroupBits,
                new GlassPolygonPoint(Zero(normal.X),Zero(normal.Y),Zero(normal.Z)),axis,sign,points,edges);
            return true;
        }
        private static bool Valid(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v)&&Math.Abs(v)<=CoordinateLimit;
        private static double Zero(double v)=>v==0?0:v;
        private static bool Equal(GlassPolygonPoint a,GlassPolygonPoint b)=>a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;
        private static int Compare(GlassPolygonPoint a,GlassPolygonPoint b)
        {int c=a.X.CompareTo(b.X);if(c==0)c=a.Y.CompareTo(b.Y);return c==0?a.Z.CompareTo(b.Z):c;}
        private static int Hash(GlassPolygonPoint p)
        {unchecked{return (p.X.GetHashCode()*31+p.Y.GetHashCode())*31+p.Z.GetHashCode();}}
        // Finite doubles are exact integers times 2^-1074. Exact signs here
        // reject zero areas, folds and crossings even at large translations.
        // Admission occurs on geometry changes, not on ordinary frame draws.
        private static BigInteger Integer(double value)
        {
            ulong bits=BitConverter.ToUInt64(BitConverter.GetBytes(value),0);
            int exponent=(int)((bits>>52)&2047);
            BigInteger mantissa=bits&0xfffffffffffffUL;
            if(exponent!=0)mantissa=(mantissa+(BigInteger.One<<52))<<(exponent-1);
            return (bits&0x8000000000000000UL)!=0?-mantissa:mantissa;
        }
        private static bool SimpleNonzeroPolygon(GlassPolygonPoint[] points,int axis)
        {
            int n=points.Length,u=(axis+1)%3,v=(axis+2)%3;
            var x=new BigInteger[n];var y=new BigInteger[n];
            for(int i=0;i<n;i++){x[i]=Integer(points[i].Axis(u));y[i]=Integer(points[i].Axis(v));}
            BigInteger area=0;
            for(int i=0;i<n;i++)
            {
                int next=(i+1)%n,previous=(i+n-1)%n;
                area+=x[i]*y[next]-y[i]*x[next];
                if(Cross(x,y,previous,i,next).IsZero&&
                   (x[previous]-x[i])*(x[next]-x[i])+(y[previous]-y[i])*(y[next]-y[i])>0)return false;
            }
            if(area.IsZero)return false;
            for(int a=0;a<n;a++)for(int b=a+1;b<n;b++)
            {
                int an=(a+1)%n,bn=(b+1)%n;
                if(an==b||bn==a)continue;
                if(Intersects(x,y,a,an,b,bn))return false;
            }
            return true;
        }
        private static BigInteger Cross(BigInteger[] x,BigInteger[] y,int a,int b,int c)=>
            (x[b]-x[a])*(y[c]-y[a])-(y[b]-y[a])*(x[c]-x[a]);
        private static bool OnSegment(BigInteger[] x,BigInteger[] y,int a,int b,int p)=>
            x[p]>=BigInteger.Min(x[a],x[b])&&x[p]<=BigInteger.Max(x[a],x[b])&&
            y[p]>=BigInteger.Min(y[a],y[b])&&y[p]<=BigInteger.Max(y[a],y[b]);
        private static bool Intersects(BigInteger[] x,BigInteger[] y,int a,int b,int c,int d)
        {
            int ac=Cross(x,y,a,b,c).Sign,ad=Cross(x,y,a,b,d).Sign,ca=Cross(x,y,c,d,a).Sign,cb=Cross(x,y,c,d,b).Sign;
            return (ac==0&&OnSegment(x,y,a,b,c))||(ad==0&&OnSegment(x,y,a,b,d))||
                (ca==0&&OnSegment(x,y,c,d,a))||(cb==0&&OnSegment(x,y,c,d,b))||
                (ac!=ad&&ac!=0&&ad!=0&&ca!=cb&&ca!=0&&cb!=0);
        }
        internal readonly struct Edge:IComparable<Edge>,IEquatable<Edge>
        {
            internal readonly GlassPolygonPoint A,B;
            internal Edge(GlassPolygonPoint a,GlassPolygonPoint b){if(Compare(a,b)>0){A=b;B=a;}else{A=a;B=b;}}
            public int CompareTo(Edge other){int c=Compare(A,other.A);return c==0?Compare(B,other.B):c;}
            public bool Equals(Edge other)=>Equal(A,other.A)&&Equal(B,other.B);
            public override bool Equals(object obj)=>obj is Edge edge&&Equals(edge);
            public override int GetHashCode(){unchecked{return Hash(A)*31+Hash(B);}}
        }
        internal sealed class BoundaryKey:IEquatable<BoundaryKey>
        {
            private readonly Edge[] edges;
            private readonly int hash;
            internal int Count=>edges.Length;
            internal BoundaryKey(Edge[] orderedEdges)
            {
                edges=orderedEdges;unchecked{int h=17;foreach(var edge in edges)h=h*31+edge.GetHashCode();hash=h;}
            }
            public bool Equals(BoundaryKey other)
            {
                if(ReferenceEquals(this,other))return true;
                if(other==null||hash!=other.hash||edges.Length!=other.edges.Length)return false;
                for(int i=0;i<edges.Length;i++)if(!edges[i].Equals(other.edges[i]))return false;
                return true;
            }
            public override bool Equals(object obj)=>obj is BoundaryKey key&&Equals(key);
            public override int GetHashCode()=>hash;
        }
    }
    internal sealed class GlassNominalCapLimits
    {
        internal readonly int MaximumOwners,MaximumCaps,MaximumCandidatesPerKey;
        internal readonly long MaximumKeyPayloadBytes;
        internal GlassNominalCapLimits(int maximumOwners=16384,int maximumCaps=65536,
            long maximumKeyPayloadBytes=16L*1024*1024,int maximumCandidatesPerKey=64)
        {
            if(maximumOwners<1||maximumCaps<1||maximumKeyPayloadBytes<1||maximumCandidatesPerKey<1)
                throw new ArgumentOutOfRangeException(nameof(maximumOwners));
            MaximumOwners=maximumOwners;MaximumCaps=maximumCaps;MaximumKeyPayloadBytes=maximumKeyPayloadBytes;
            MaximumCandidatesPerKey=maximumCandidatesPerKey;
        }
    }
    internal sealed class GlassNominalCapModel
    {
        private readonly GlassNominalCapLimits limits;
        private readonly Dictionary<int,Owner> owners=new Dictionary<int,Owner>();
        private readonly Dictionary<Key,List<Node>> buckets=new Dictionary<Key,List<Node>>();
        private readonly Dictionary<int,uint> masks=new Dictionary<int,uint>(),changes=new Dictionary<int,uint>();
        internal GlassNominalCapModel(GlassNominalCapLimits limits=null){this.limits=limits??new GlassNominalCapLimits();}
        internal bool Faulted {get;private set;}
        internal string FailureReason {get;private set;}
        internal int OwnerCount=>owners.Count;
        internal int CapCount {get;private set;}
        internal long KeyPayloadBytes {get;private set;}
        internal uint GetMask(int id)=>masks.TryGetValue(id,out uint mask)?mask:0;
        internal bool Upsert(long entity,int buildId,byte health,IReadOnlyList<GlassNominalCap> caps)
        {
            if(Faulted)return false;
            if(buildId<=0)return false;
            // Even unsupported/malformed replacements must revoke the former
            // owner and every neighbour which used its old nominal identity.
            Remove(buildId);
            if(health!=255)return true;
            if(caps==null||caps.Count>8)return false;
            int count=caps.Count;if(count==0)return true;
            var nodes=new Node[count];var additions=new Dictionary<Key,int>();uint bits=0;long bytes=0;
            for(int i=0;i<count;i++)
            {
                var cap=caps[i];if(cap==null||(bits&(1u<<cap.Bit))!=0)return false;
                bits|=1u<<cap.Bit;bytes+=cap.KeyPayloadBytes;
                var key=new Key(entity,cap);nodes[i]=new Node(buildId,cap,key);
                additions.TryGetValue(key,out int amount);additions[key]=amount+1;
            }
            if((long)owners.Count+1>limits.MaximumOwners)return Fail("nominal owner limit");
            if((long)CapCount+count>limits.MaximumCaps)return Fail("nominal cap limit");
            if(bytes>limits.MaximumKeyPayloadBytes-KeyPayloadBytes)return Fail("nominal key payload limit");
            foreach(var pair in additions)
            {
                int previous=buckets.TryGetValue(pair.Key,out var bucket)?bucket.Count:0;
                if((long)previous+pair.Value>limits.MaximumCandidatesPerKey)return Fail("nominal candidates per key limit");
            }
            owners.Add(buildId,new Owner(nodes,bytes));CapCount+=count;KeyPayloadBytes+=bytes;
            var affected=new HashSet<int>{buildId};
            foreach(var node in nodes)
            {
                if(!buckets.TryGetValue(node.Key,out var bucket))buckets.Add(node.Key,bucket=new List<Node>());
                foreach(var other in bucket)affected.Add(other.BuildId);
                bucket.Add(node);
            }
            Recompute(affected);return true;
        }
        internal void Remove(int id)
        {
            if(!owners.TryGetValue(id,out var owner))return;
            owners.Remove(id);CapCount-=owner.Nodes.Length;KeyPayloadBytes-=owner.Bytes;
            var affected=new HashSet<int>{id};
            foreach(var node in owner.Nodes)
            {
                var bucket=buckets[node.Key];bucket.Remove(node);
                foreach(var other in bucket)affected.Add(other.BuildId);
                if(bucket.Count==0)buckets.Remove(node.Key);
            }
            Recompute(affected);
        }
        internal void Clear(){ClearState();Faulted=false;FailureReason=null;}
        internal void DrainChanges(List<GlassPolygonCoverageChange> output)
        {
            if(output==null)throw new ArgumentNullException(nameof(output));
            foreach(var pair in changes)output.Add(new GlassPolygonCoverageChange(pair.Key,pair.Value));
            changes.Clear();
        }
        private void ClearState()
        {
            foreach(var pair in masks)changes[pair.Key]=0;
            owners.Clear();buckets.Clear();masks.Clear();CapCount=0;KeyPayloadBytes=0;
        }
        private bool Fail(string reason){ClearState();Faulted=true;FailureReason=reason;return false;}
        private void SetMask(int id,uint value)
        {
            if(GetMask(id)!=value)changes[id]=value;
            if(value==0)masks.Remove(id);else masks[id]=value;
        }
        private void Recompute(HashSet<int> affected)
        {
            foreach(int id in affected)
            {
                uint mask=0;
                if(owners.TryGetValue(id,out var owner))foreach(var node in owner.Nodes)
                {
                    var cap=node.Cap;
                    foreach(var other in buckets[node.Key])
                    {
                        var mate=other.Cap;
                        if(other.BuildId==id||cap.NormalAxis!=mate.NormalAxis||cap.NormalSign!=-mate.NormalSign||
                           (cap.CompatibleGroupBits&(1u<<mate.Bit))==0||(mate.CompatibleGroupBits&(1u<<cap.Bit))==0)continue;
                        mask|=1u<<cap.Bit;break;
                    }
                }
                SetMask(id,mask);
            }
        }
        private sealed class Owner
        {
            internal readonly Node[] Nodes;internal readonly long Bytes;
            internal Owner(Node[] nodes,long bytes){Nodes=nodes;Bytes=bytes;}
        }
        private sealed class Node
        {
            internal readonly int BuildId;internal readonly GlassNominalCap Cap;internal readonly Key Key;
            internal Node(int id,GlassNominalCap cap,Key key){BuildId=id;Cap=cap;Key=key;}
        }
        private readonly struct Key:IEquatable<Key>
        {
            private readonly long entity;private readonly int type,family;private readonly GlassNominalCap.BoundaryKey boundary;
            internal Key(long entity,GlassNominalCap cap){this.entity=entity;type=cap.SourceType;family=cap.ProfileFamily;boundary=cap.Identity;}
            public bool Equals(Key other)=>entity==other.entity&&type==other.type&&family==other.family&&boundary.Equals(other.boundary);
            public override bool Equals(object obj)=>obj is Key key&&Equals(key);
            public override int GetHashCode(){unchecked{return ((entity.GetHashCode()*31+type)*31+family)*31+boundary.GetHashCode();}}
        }
    }
}
