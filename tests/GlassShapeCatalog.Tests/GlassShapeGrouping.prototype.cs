using System;
using System.Collections.Generic;
using System.Numerics;
namespace GraphicalTweaks
{
    internal sealed class GlassShapeGrouping
    {
        internal int GroupCount=>GroupTriangles.Length;
        internal readonly uint[] TriangleFaceBits;
        internal readonly int[][] GroupTriangles;
        private GlassShapeGrouping(uint[] labels,int[][] groups){TriangleFaceBits=labels;GroupTriangles=groups;}
        internal static bool TryCreate(GlassShapeFloat3[] positions,int actual,int padded,bool axisOnly,
            GlassShapeContactCanonicalizer policy,out GlassShapeGrouping result,out string reason)
        {
            result=null;reason=null;
            if(positions==null||policy==null||actual<1||actual>21845||positions.Length!=actual*3||
                padded<actual||padded%12!=0||padded>21852){reason="invalid bounded triangle range";return false;}
            var keys=new Dictionary<Plane,int>();var groups=new List<List<int>>();var labels=new uint[padded];
            for(int triangle=0;triangle<actual;triangle++)
            {
                if(!Plane.TryCreate(positions[triangle*3],positions[triangle*3+1],positions[triangle*3+2],out var raw))
                {reason="degenerate raw source triangle";return false;}
                if(!policy.TryCanonicalize(positions[triangle*3],out var a)||
                   !policy.TryCanonicalize(positions[triangle*3+1],out var b)||
                   !policy.TryCanonicalize(positions[triangle*3+2],out var c))
                {reason="unknown source position";return false;}
                if(!Plane.TryCreate(a,b,c,out var plane)){reason="degenerate canonical triangle";return false;}
                if(!raw.SameOrientation(plane)){reason="canonical source triangle reversed orientation";return false;}
                if(axisOnly&&!plane.AxisAligned)continue;
                if(!keys.TryGetValue(plane,out int bit))
                {
                    if(groups.Count==8){reason="more than eight face groups";return false;}
                    bit=groups.Count;keys.Add(plane,bit);groups.Add(new List<int>());
                }
                labels[triangle]=1u<<bit;groups[bit].Add(triangle);
            }
            if(groups.Count==0){reason="no eligible source plane";return false;}
            var arrays=new int[groups.Count][];for(int i=0;i<arrays.Length;i++)arrays[i]=groups[i].ToArray();
            result=new GlassShapeGrouping(labels,arrays);return true;
        }

        // A float is an integer times 2^-149. Exact cross products and a positive
        // gcd provide an oriented plane key without angle/distance tolerances.
        // Only source-catalog construction pays this cost; instances retain lists
        // of the original triangle indices, including concave and holed caps.
        readonly struct Plane:IEquatable<Plane>
        {
            readonly BigInteger X,Y,Z,D;
            Plane(BigInteger x,BigInteger y,BigInteger z,BigInteger d){X=x;Y=y;Z=z;D=d;}
            internal bool AxisAligned=>(X.IsZero?0:1)+(Y.IsZero?0:1)+(Z.IsZero?0:1)==1;
            internal bool SameOrientation(Plane p)=>X*p.X+Y*p.Y+Z*p.Z>0;
            internal static bool TryCreate(GlassShapeFloat3 a,GlassShapeFloat3 b,GlassShapeFloat3 c,out Plane p)
            {
                p=default;BigInteger ax=I(a.X),ay=I(a.Y),az=I(a.Z);
                BigInteger bx=I(b.X)-ax,by=I(b.Y)-ay,bz=I(b.Z)-az,cx=I(c.X)-ax,cy=I(c.Y)-ay,cz=I(c.Z)-az;
                BigInteger x=by*cz-bz*cy,y=bz*cx-bx*cz,z=bx*cy-by*cx,d=-(x*ax+y*ay+z*az);
                BigInteger gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(x),BigInteger.Abs(y));
                gcd=BigInteger.GreatestCommonDivisor(gcd,BigInteger.Abs(z));
                if(gcd.IsZero)return false;
                gcd=BigInteger.GreatestCommonDivisor(gcd,BigInteger.Abs(d));
                p=new Plane(x/gcd,y/gcd,z/gcd,d/gcd);return true;
            }
            static BigInteger I(float f)
            {
                uint bits=BitConverter.ToUInt32(BitConverter.GetBytes(f),0);int exponent=(int)((bits>>23)&255);
                BigInteger v=bits&0x7fffff;if(exponent!=0)v=(v+0x800000)<<(exponent-1);
                return (bits&0x80000000)!=0?-v:v;
            }
            public bool Equals(Plane p)=>X==p.X&&Y==p.Y&&Z==p.Z&&D==p.D;
            public override bool Equals(object o)=>o is Plane p&&Equals(p);
            public override int GetHashCode()=>unchecked(((X.GetHashCode()*397^Y.GetHashCode())*397^Z.GetHashCode())*397^D.GetHashCode());
        }
    }
}
