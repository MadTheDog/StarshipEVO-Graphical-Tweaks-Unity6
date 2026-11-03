using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace GraphicalTweaks
{
    // Exact source-triangle weights. Default(struct) is deliberately invalid.
    internal readonly struct GlassBarycentric : IEquatable<GlassBarycentric>
    {
        internal readonly BigInteger A,B,C,D;
        internal bool IsValid=>D.Sign>0&&A.Sign>=0&&B.Sign>=0&&C.Sign>=0&&A+B+C==D&&
            (A>>512).IsZero&&(B>>512).IsZero&&(C>>512).IsZero&&(D>>512).IsZero;
        internal GlassBarycentric(BigInteger a,BigInteger b,BigInteger c,BigInteger d)
        {
            if(d.Sign<=0||a.Sign<0||b.Sign<0||c.Sign<0||a+b+c!=d)
                throw new ArgumentOutOfRangeException(nameof(d),"Barycentric weights must be nonnegative and sum to a positive denominator.");
            BigInteger divisor=BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(a,b),BigInteger.GreatestCommonDivisor(c,d));
            A=a/divisor;B=b/divisor;C=c/divisor;D=d/divisor;
            if(!IsValid)throw new ArgumentOutOfRangeException(nameof(d),"Barycentric precision limit exceeded.");
        }
        public bool Equals(GlassBarycentric other)=>A==other.A&&B==other.B&&C==other.C&&D==other.D;
        public override bool Equals(object obj)=>obj is GlassBarycentric other&&Equals(other);
        public override int GetHashCode()=>unchecked(((A.GetHashCode()*397^B.GetHashCode())*397^C.GetHashCode())*397^D.GetHashCode());
        public override string ToString()=>A.ToString(CultureInfo.InvariantCulture)+","+B.ToString(CultureInfo.InvariantCulture)+","+
            C.ToString(CultureInfo.InvariantCulture)+"/"+D.ToString(CultureInfo.InvariantCulture);
    }
    internal readonly struct GlassBarycentricTriangle
    {
        internal readonly GlassBarycentric A,B,C;
        internal GlassBarycentricTriangle(GlassBarycentric a,GlassBarycentric b,GlassBarycentric c)
        {
            if(!a.IsValid||!b.IsValid||!c.IsValid)throw new ArgumentException("Invalid source weights.");
            A=a;B=b;C=c;
        }
    }
    internal sealed class GlassTriangleRemainder
    {
        internal readonly int TriangleIndex;
        internal readonly bool Changed;
        internal IReadOnlyList<GlassBarycentricTriangle> Triangles {get;}
        internal GlassTriangleRemainder(int triangleIndex,bool changed,IReadOnlyList<GlassBarycentricTriangle> triangles)
        {
            if(triangleIndex<0||triangles==null)throw new ArgumentOutOfRangeException(nameof(triangleIndex));
            var copy=new GlassBarycentricTriangle[triangles.Count];
            for(int i=0;i<copy.Length;i++)copy[i]=triangles[i];
            TriangleIndex=triangleIndex;Changed=changed;Triangles=Array.AsReadOnly(copy);
        }
    }
    internal sealed class GlassCapClipResult
    {
        internal readonly bool Changed;
        internal IReadOnlyList<GlassTriangleRemainder> Triangles {get;}
        internal GlassCapClipResult(IReadOnlyList<GlassTriangleRemainder> triangles)
        {
            if(triangles==null)throw new ArgumentNullException(nameof(triangles));
            var copy=new GlassTriangleRemainder[triangles.Count];bool changed=false;
            for(int i=0;i<copy.Length;i++)
            {copy[i]=triangles[i]??throw new ArgumentException("Null triangle remainder.");changed|=copy[i].Changed;}
            Triangles=Array.AsReadOnly(copy);Changed=changed;
        }
    }
}
