using System;

namespace GraphicalTweaks
{
    public readonly struct GlassShapeFloat3
    {
        public readonly float X, Y, Z;
        public GlassShapeFloat3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    // Logical row/column order, independent of Unity/HLSL buffer storage.
    public readonly struct GlassShapeRotation
    {
        public readonly float M00, M01, M02, M03, M10, M11, M12, M13;
        public readonly float M20, M21, M22, M23, M30, M31, M32, M33;
        public GlassShapeRotation(float[] rowMajor)
        {
            if (rowMajor == null || rowMajor.Length != 16) throw new ArgumentException("Exactly 16 coefficients required.", nameof(rowMajor));
            M00=rowMajor[0];M01=rowMajor[1];M02=rowMajor[2];M03=rowMajor[3];
            M10=rowMajor[4];M11=rowMajor[5];M12=rowMajor[6];M13=rowMajor[7];
            M20=rowMajor[8];M21=rowMajor[9];M22=rowMajor[10];M23=rowMajor[11];
            M30=rowMajor[12];M31=rowMajor[13];M32=rowMajor[14];M33=rowMajor[15];
        }
    }

    public readonly struct GlassShapeDeformationMaterial
    {
        public readonly int ResizeMode, VertexMode, AlphaMode, BorderIndex, BuildingOffset;
        public readonly float ResizeUnit, GeometryLow, GeometryHigh;
        public GlassShapeDeformationMaterial(int resizeMode, int vertexMode, int alphaMode,
            float resizeUnit, float geometryLow, float geometryHigh, int borderIndex = -1, int buildingOffset = 0)
        {
            ResizeMode=resizeMode;VertexMode=vertexMode;AlphaMode=alphaMode;ResizeUnit=resizeUnit;
            GeometryLow=geometryLow;GeometryHigh=geometryHigh;BorderIndex=borderIndex;BuildingOffset=buildingOffset;
        }
    }

    public readonly struct GlassShapeDeformationInput
    {
        public readonly GlassShapeFloat3 SourcePosition, SourceNormal, BoundsCenter, BoundsSize, BrickPosition;
        public readonly GlassShapeDeformationMaterial Material;
        public readonly int CountX, CountY, CountZ;
        public readonly float BrickUnit;
        public readonly GlassShapeRotation Rotation;
        public GlassShapeDeformationInput(GlassShapeFloat3 sourcePosition, GlassShapeFloat3 sourceNormal,
            GlassShapeFloat3 boundsCenter, GlassShapeFloat3 boundsSize, GlassShapeDeformationMaterial material,
            int countX, int countY, int countZ, float brickUnit, GlassShapeRotation rotation, GlassShapeFloat3 brickPosition)
        {
            SourcePosition=sourcePosition;SourceNormal=sourceNormal;BoundsCenter=boundsCenter;BoundsSize=boundsSize;
            Material=material;CountX=countX;CountY=countY;CountZ=countZ;BrickUnit=brickUnit;Rotation=rotation;BrickPosition=brickPosition;
        }
    }

    public static class GlassShapeDeformationModel
    {
        public static int DecodeRotationIndex(uint rotationAndMaterial) => (int)(rotationAndMaterial & 31u);

        // Returns the shader's rendererPosition, before entity/object transforms.
        // Eligibility beyond these fixed material controls (identity, health, pass,
        // source pool lifetime and group ownership) belongs to the caller.
        public static bool TryDeform(GlassShapeDeformationInput input, out GlassShapeFloat3 entityLocalPosition)
        {
            entityLocalPosition=default(GlassShapeFloat3);
            var m=input.Material;
            if((m.ResizeMode!=0&&m.ResizeMode!=7&&m.ResizeMode!=8)||m.VertexMode!=0||m.AlphaMode!=2||m.BorderIndex!=-1||m.BuildingOffset!=0||
                !Finite(m.ResizeUnit)||m.ResizeUnit<=0||!Finite(m.GeometryLow)||!Finite(m.GeometryHigh)||m.GeometryLow>m.GeometryHigh||
                input.CountX<0||input.CountX>15||input.CountY<0||input.CountY>15||input.CountZ<0||input.CountZ>15||
                !Finite(input.BrickUnit)||input.BrickUnit<=0||!Finite(input.SourcePosition)||!Finite(input.SourceNormal)||
                !Finite(input.BoundsCenter)||!Finite(input.BoundsSize)||!Finite(input.BrickPosition)||
                input.BoundsSize.X<=0||input.BoundsSize.Y<=0||input.BoundsSize.Z<=0||!Rigid(input.Rotation))return false;

            float x=input.SourcePosition.X,y=input.SourcePosition.Y,z=input.SourcePosition.Z;
            if(m.ResizeMode==0)
            {
                float rx=x-input.BoundsCenter.X,ry=y-input.BoundsCenter.Y,rz=z-input.BoundsCenter.Z;
                float ex=m.ResizeUnit*input.CountX,ey=m.ResizeUnit*input.CountY,ez=m.ResizeUnit*input.CountZ;
                if(!Finite(rx)||!Finite(ry)||!Finite(rz)||!Finite(ex)||!Finite(ey)||!Finite(ez))return false;
                x=(rx+ex*Factor(rx,m.GeometryLow,m.GeometryHigh))+input.BoundsCenter.X;
                y=(ry+ey*Factor(ry,m.GeometryLow,m.GeometryHigh))+input.BoundsCenter.Y;
                z=(rz+ez*Factor(rz,m.GeometryLow,m.GeometryHigh))+input.BoundsCenter.Z;
            }
            else
            {
                float ax=input.BoundsCenter.X-.5f*input.BoundsSize.X;
                float ay=input.BoundsCenter.Y-.5f*input.BoundsSize.Y;
                float az=input.BoundsCenter.Z-.5f*input.BoundsSize.Z;
                float rx=x-ax,ry=y-ay,rz=z-az;
                if(!Finite(ax)||!Finite(ay)||!Finite(az)||!Finite(rx)||!Finite(ry)||!Finite(rz))return false;
                float squared=rx*rx+ry*ry;
                if(m.ResizeMode==8)squared=squared+rz*rz;
                if(!Finite(squared))return false;
                float radius=(float)Math.Sqrt(squared);
                if(radius>.01f)
                {
                    // Native radius linkage uses raw countX, independent of ResizeUnit.
                    float factor=(radius+input.CountX)/radius;
                    rx*=factor;ry*=factor;
                    if(m.ResizeMode==8)rz*=factor;
                }
                if(m.ResizeMode==7&&rz>.5f)rz+=input.CountZ;
                x=ax+rx;y=ay+ry;z=az+rz;
            }
            if(!Finite(x)||!Finite(y)||!Finite(z))return false;
            x*=input.BrickUnit;y*=input.BrickUnit;z*=input.BrickUnit;
            if(!Finite(x)||!Finite(y)||!Finite(z))return false;
            var r=input.Rotation;
            // No snapping, quantization or explicit fused multiply-add. GPU mad /
            // reciprocal-sqrt instruction choices can differ; exact parity is not promised.
            float ox=((r.M00*x+r.M01*y)+r.M02*z)+input.BrickPosition.X;
            float oy=((r.M10*x+r.M11*y)+r.M12*z)+input.BrickPosition.Y;
            float oz=((r.M20*x+r.M21*y)+r.M22*z)+input.BrickPosition.Z;
            if(!Finite(ox)||!Finite(oy)||!Finite(oz))return false;
            entityLocalPosition=new GlassShapeFloat3(ox,oy,oz);
            return true;
        }

        static float Factor(float relative,float low,float high)=>relative>high?1f:relative>low?.5f:0f;
        static bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
        static bool Finite(GlassShapeFloat3 p)=>Finite(p.X)&&Finite(p.Y)&&Finite(p.Z);

        static bool Rigid(GlassShapeRotation r)
        {
            if(!Finite(r.M00)||!Finite(r.M01)||!Finite(r.M02)||!Finite(r.M03)||
               !Finite(r.M10)||!Finite(r.M11)||!Finite(r.M12)||!Finite(r.M13)||
               !Finite(r.M20)||!Finite(r.M21)||!Finite(r.M22)||!Finite(r.M23)||
               !Finite(r.M30)||!Finite(r.M31)||!Finite(r.M32)||!Finite(r.M33)||
               r.M03!=0||r.M13!=0||r.M23!=0||r.M30!=0||r.M31!=0||r.M32!=0||r.M33!=1)return false;
            // Admission tolerance only; preserves every coefficient as provided.
            // Captured native quaternion-derived matrices are not exact signed permutations.
            const double tolerance=0.00001;
            double a=r.M00,b=r.M01,c=r.M02,d=r.M10,e=r.M11,f=r.M12,g=r.M20,h=r.M21,i=r.M22;
            return Math.Abs(a*a+b*b+c*c-1)<=tolerance&&Math.Abs(d*d+e*e+f*f-1)<=tolerance&&Math.Abs(g*g+h*h+i*i-1)<=tolerance&&
                Math.Abs(a*d+b*e+c*f)<=tolerance&&Math.Abs(a*g+b*h+c*i)<=tolerance&&Math.Abs(d*g+e*h+f*i)<=tolerance&&
                Math.Abs(a*(e*i-f*h)-b*(d*i-f*g)+c*(d*h-e*g)-1)<=tolerance;
        }
    }
}
