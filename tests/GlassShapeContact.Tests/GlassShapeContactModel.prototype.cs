using System;
using System.Collections.Generic;
namespace GraphicalTweaks
{
    // Contact semantics only: these values never replace GPU vertices, native
    // matrices, collision models or placement coordinates. Prototype, not activated.
    internal sealed class GlassShapeContactCanonicalizer
    {
        internal const double SourceDiameter=1e-6;
        private readonly Dictionary<float,float> magnitudes;
        internal double MaximumDisplacement {get;}
        internal int SourceMagnitudeCount=>magnitudes.Count;
        internal int CanonicalMagnitudeCount {get;}
        private GlassShapeContactCanonicalizer(Dictionary<float,float> values,double displacement,int count)
        {magnitudes=values;MaximumDisplacement=displacement;CanonicalMagnitudeCount=count;}
        internal static bool TryCreate(IReadOnlyList<GlassShapeFloat3> positions,out GlassShapeContactCanonicalizer policy)
        {
            policy=null;if(positions==null||positions.Count==0||positions.Count>65536)return false;
            var unique=new HashSet<float>();
            foreach(var p in positions)foreach(float v in new[]{p.X,p.Y,p.Z})
            {if(!Finite(v)||Math.Abs(v)>64)return false;unique.Add(Math.Abs(v));}
            var sorted=new List<float>(unique);sorted.Sort();var map=new Dictionary<float,float>();double displacement=0;int clusters=0;
            for(int start=0;start<sorted.Count;)
            {
                int end=start+1;while(end<sorted.Count&&(double)sorted[end]-sorted[start]<=SourceDiameter)end++;
                float target=sorted[start];
                for(int j=start;j<end;j++)if((double)sorted[j]*2==Math.Round((double)sorted[j]*2)){target=sorted[j];break;}
                for(int j=start;j<end;j++){map.Add(sorted[j],target);displacement=Math.Max(displacement,Math.Abs((double)sorted[j]-target));}
                clusters++;start=end;
            }
            policy=new GlassShapeContactCanonicalizer(map,displacement,clusters);return true;
        }
        internal bool TryCanonicalize(GlassShapeFloat3 source,out GlassShapeFloat3 point)
        {
            point=default;
            if(!Known(source.X,out float x)||!Known(source.Y,out float y)||!Known(source.Z,out float z))return false;
            point=new GlassShapeFloat3(x,y,z);return true;
        }
        private bool Known(float value,out float result)
        {
            result=0;if(!Finite(value)||!magnitudes.TryGetValue(Math.Abs(value),out float target))return false;
            result=value<0?-target:target;return true;
        }
        // Only the caller's derived radial anchor is permitted this nearest-source
        // lookup. Instance placement, bounds size and deformed points never use it.
        internal bool TryCanonicalizeRadialAnchor(GlassShapeFloat3 anchor,out GlassShapeFloat3 point)
        {
            point=default;
            if(!Anchor(anchor.X,out float x)||!Anchor(anchor.Y,out float y)||!Anchor(anchor.Z,out float z))return false;
            point=new GlassShapeFloat3(x,y,z);return true;
        }
        private bool Anchor(float value,out float result)
        {
            result=0;if(!Finite(value))return false;
            float best=0;double distance=double.PositiveInfinity;
            foreach(float candidate in magnitudes.Values)
            {
                double d=Math.Abs((double)Math.Abs(value)-candidate);
                if(d<distance||(d==distance&&candidate<best)){best=candidate;distance=d;}
            }
            if(distance>SourceDiameter)return false;
            result=value<0?-best:best;return true;
        }
        private static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
    }

    internal static class GlassShapeContactModel
    {
        internal const double RotationCoefficientLimit=1.1920928955078125e-7;
        private static readonly GlassShapeRotation Identity=new GlassShapeRotation(new float[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1});
        internal static bool TryCanonicalRotation(GlassShapeRotation r,out GlassShapeRotation canonical)
        {
            canonical=default;
            if(r.M03!=0||r.M13!=0||r.M23!=0||r.M30!=0||r.M31!=0||r.M32!=0||r.M33!=1)return false;
            float[] values={r.M00,r.M01,r.M02,0,r.M10,r.M11,r.M12,0,r.M20,r.M21,r.M22,0,0,0,0,1};
            for(int row=0;row<3;row++)for(int col=0;col<3;col++)
            {
                int at=row*4+col;float v=values[at];double n=Math.Round(v);
                if(float.IsNaN(v)||float.IsInfinity(v)||n< -1||n>1||Math.Abs(v-n)>RotationCoefficientLimit)return false;
                values[at]=(float)n;
            }
            for(int i=0;i<3;i++)
            {
                int row=0,col=0;for(int j=0;j<3;j++){if(values[i*4+j]!=0)row++;if(values[j*4+i]!=0)col++;}
                if(row!=1||col!=1)return false;
            }
            float a=values[0],b=values[1],c=values[2],d=values[4],e=values[5],f=values[6],g=values[8],h=values[9],i2=values[10];
            if(a*(e*i2-f*h)-b*(d*i2-f*g)+c*(d*h-e*g)!=1)return false;
            canonical=new GlassShapeRotation(values);return true;
        }
        internal static bool TryDeform(GlassShapeDeformationInput input,GlassShapeContactCanonicalizer policy,
            GlassPolygonPoint exactPlacement,out GlassPolygonPoint contact)
        {
            contact=default;
            if(policy==null||!Grid(exactPlacement.X)||!Grid(exactPlacement.Y)||!Grid(exactPlacement.Z)||
                input.BrickPosition.X!=(float)exactPlacement.X||input.BrickPosition.Y!=(float)exactPlacement.Y||input.BrickPosition.Z!=(float)exactPlacement.Z||
                !TryCanonicalRotation(input.Rotation,out var rotation)||!policy.TryCanonicalize(input.SourcePosition,out var source))return false;
            var center=input.BoundsCenter;
            if(input.Material.ResizeMode==7||input.Material.ResizeMode==8)
            {
                var rawAnchor=Anchor(center,input.BoundsSize);
                if(!policy.TryCanonicalizeRadialAnchor(rawAnchor,out var anchor))return false;
                center=new GlassShapeFloat3(anchor.X+.5f*input.BoundsSize.X,anchor.Y+.5f*input.BoundsSize.Y,anchor.Z+.5f*input.BoundsSize.Z);
                if(!Equal(Anchor(center,input.BoundsSize),anchor)||!SameRadialBranches(input.SourcePosition,rawAnchor,source,anchor,input.Material.ResizeMode))return false;
            }
            else if(input.Material.ResizeMode==0)
            {
                if(Factor(input.SourcePosition.X-center.X,input.Material)!=Factor(source.X-center.X,input.Material)||
                    Factor(input.SourcePosition.Y-center.Y,input.Material)!=Factor(source.Y-center.Y,input.Material)||
                    Factor(input.SourcePosition.Z-center.Z,input.Material)!=Factor(source.Z-center.Z,input.Material))return false;
            }
            if(!GlassShapeDeformationModel.TryDeform(Local(input,input.SourcePosition,input.BoundsCenter),out var raw)||
                !GlassShapeDeformationModel.TryDeform(Local(input,source,center),out var adjusted))return false;
            double dx=(double)raw.X-adjusted.X,dy=(double)raw.Y-adjusted.Y,dz=(double)raw.Z-adjusted.Z;
            double bound=2e-6*(1+Math.Max(input.CountX,Math.Max(input.CountY,input.CountZ)))*input.BrickUnit;
            if(dx*dx+dy*dy+dz*dz>bound*bound)return false;
            contact=new GlassPolygonPoint(rotation.M00*(double)adjusted.X+rotation.M01*(double)adjusted.Y+rotation.M02*(double)adjusted.Z+exactPlacement.X,
                rotation.M10*(double)adjusted.X+rotation.M11*(double)adjusted.Y+rotation.M12*(double)adjusted.Z+exactPlacement.Y,
                rotation.M20*(double)adjusted.X+rotation.M21*(double)adjusted.Y+rotation.M22*(double)adjusted.Z+exactPlacement.Z);
            if(!Coordinate(contact.X)||!Coordinate(contact.Y)||!Coordinate(contact.Z)){contact=default;return false;}
            return true;
        }
        private static GlassShapeDeformationInput Local(GlassShapeDeformationInput input,GlassShapeFloat3 source,GlassShapeFloat3 center)
            =>new GlassShapeDeformationInput(source,input.SourceNormal,center,input.BoundsSize,input.Material,input.CountX,input.CountY,input.CountZ,input.BrickUnit,Identity,default);
        private static GlassShapeFloat3 Anchor(GlassShapeFloat3 c,GlassShapeFloat3 s)=>new GlassShapeFloat3(c.X-.5f*s.X,c.Y-.5f*s.Y,c.Z-.5f*s.Z);
        private static bool Equal(GlassShapeFloat3 a,GlassShapeFloat3 b)=>a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;
        private static int Factor(float v,GlassShapeDeformationMaterial m)=>v>m.GeometryHigh?2:v>m.GeometryLow?1:0;
        private static bool SameRadialBranches(GlassShapeFloat3 p,GlassShapeFloat3 a,GlassShapeFloat3 q,GlassShapeFloat3 b,int mode)
        {
            float x=p.X-a.X,y=p.Y-a.Y,z=p.Z-a.Z,u=q.X-b.X,v=q.Y-b.Y,w=q.Z-b.Z;
            float squared=x*x+y*y,other=u*u+v*v;if(mode==8){squared+=z*z;other+=w*w;}
            return ((float)Math.Sqrt(squared)>.01f)==((float)Math.Sqrt(other)>.01f)&&(mode!=7||(z>.5f)==(w>.5f));
        }
        private static bool Coordinate(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v)&&Math.Abs(v)<=1048576;
        private static bool Grid(double v)=>Coordinate(v)&&v*32==Math.Truncate(v*32);
    }
}
