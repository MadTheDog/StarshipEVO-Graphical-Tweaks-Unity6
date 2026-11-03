using System;

namespace GraphicalTweaks
{
    public struct DecalWorkBounds
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public DecalWorkBounds(double x0, double y0, double z0, double x1, double y1, double z1)
        { MinX=x0; MinY=y0; MinZ=z0; MaxX=x1; MaxY=y1; MaxZ=z1; }
        public bool Valid => DecalVisibilityWorkModel.Finite(MinX) && DecalVisibilityWorkModel.Finite(MinY) && DecalVisibilityWorkModel.Finite(MinZ) &&
            DecalVisibilityWorkModel.Finite(MaxX) && DecalVisibilityWorkModel.Finite(MaxY) && DecalVisibilityWorkModel.Finite(MaxZ) && MinX<=MaxX && MinY<=MaxY && MinZ<=MaxZ;
    }

    // Input coefficients are exact promoted native floats. No Unity dependency.
    public struct DecalWorkMatrix
    {
        public double M00,M01,M02,M03,M10,M11,M12,M13,M20,M21,M22,M23,M30,M31,M32,M33;
        public static DecalWorkMatrix Identity => new DecalWorkMatrix { M00=1,M11=1,M22=1,M33=1 };
        public double this[int row, int col]
        {
            get
            {
                switch (row*4+col)
                {
                    case 0:return M00; case 1:return M01; case 2:return M02; case 3:return M03;
                    case 4:return M10; case 5:return M11; case 6:return M12; case 7:return M13;
                    case 8:return M20; case 9:return M21; case 10:return M22; case 11:return M23;
                    case 12:return M30; case 13:return M31; case 14:return M32; default:return M33;
                }
            }
        }
    }

    public struct DecalWorkTransform
    {
        public double X,Y,Z,QX,QY,QZ,QW,Scale;
    }

    // Include ONLY authoritative original and native Aabb.SetSize(Size*10) bounds.
    // Historical space deliberately stays until the containing group is deleted.
    public sealed class DecalWorkEnvelope
    {
        public DecalWorkBounds Bounds { get; private set; }
        public bool HasBounds { get; private set; }
        public bool Invalid { get; private set; }
        public bool Usable => HasBounds && !Invalid;
        public void Include(DecalWorkBounds original, DecalWorkBounds nativeMaximum)
        {
            if (!original.Valid || !nativeMaximum.Valid) { Invalid=true; return; }
            IncludeBox(original); IncludeBox(nativeMaximum);
        }
        private void IncludeBox(DecalWorkBounds box)
        {
            if (!HasBounds) { Bounds=box; HasBounds=true; return; }
            var b=Bounds;
            Bounds=new DecalWorkBounds(Math.Min(b.MinX,box.MinX),Math.Min(b.MinY,box.MinY),Math.Min(b.MinZ,box.MinZ),
                Math.Max(b.MaxX,box.MaxX),Math.Max(b.MaxY,box.MaxY),Math.Max(b.MaxZ,box.MaxZ));
        }
    }

    // Shared decision policy keeps the early-return wakeup contract testable.
    public struct DecalWorkState
    {
        public bool Skipped { get; private set; }
        public bool Decide(bool enabled, bool adaptive, bool full, bool scan, bool moved, bool outside, out bool woke)
        {
            bool maySkip=enabled && adaptive && !full && outside;
            woke=Skipped && !maySkip;
            if (full || woke) { Skipped=false; return true; }
            if (scan && moved) { Skipped=maySkip; return !maySkip; }
            return false;
        }
    }

    public static class DecalVisibilityWorkModel
    {
        // 1e-4 is >800 float epsilons: covers float concatenation in either
        // association, GPU point transforms and AABB arithmetic. Use absolute
        // uncombined products so large world-origin cancellation cannot hide it.
        public const double RelativeOutwardAllowance=0.0001;
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static bool SameUploadedTransform(DecalWorkTransform current, DecalWorkTransform uploaded, int uploadedValid, bool fresh, bool indexValid)
        {
            if (!fresh || !indexValid || uploadedValid != 1 || !ValidTransform(current) || !ValidTransform(uploaded)) return false;
            // Equality is deliberate: even a tiny post-upload native command
            // invalidates the proof rather than assuming the GPU saw live TRS.
            return current.X==uploaded.X && current.Y==uploaded.Y && current.Z==uploaded.Z &&
                current.QX==uploaded.QX && current.QY==uploaded.QY && current.QZ==uploaded.QZ && current.QW==uploaded.QW && current.Scale==uploaded.Scale;
        }
        private static bool ValidTransform(DecalWorkTransform t)
        {
            if (!Finite(t.X) || !Finite(t.Y) || !Finite(t.Z) || !Finite(t.QX) || !Finite(t.QY) || !Finite(t.QZ) || !Finite(t.QW) || !Finite(t.Scale) || t.Scale<=0) return false;
            double norm=t.QX*t.QX+t.QY*t.QY+t.QZ*t.QZ+t.QW*t.QW;
            return Finite(norm) && Math.Abs(norm-1)<=0.000001;
        }
        public static bool IsOutside(DecalWorkBounds bounds, DecalWorkMatrix near, bool hasFar, DecalWorkMatrix far, DecalWorkMatrix camera, DecalWorkMatrix local)
        {
            if (!bounds.Valid || !Affine(camera) || !Affine(local) || !Perspective(near) || (hasFar && !Perspective(far))) return false;
            if (MaxCoordinate(bounds)>1e18) return false;
            return OutsideProjection(bounds,near,camera,local) && (!hasFar || OutsideProjection(bounds,far,camera,local));
        }
        private static double MaxCoordinate(DecalWorkBounds b) => Math.Max(Math.Max(Math.Abs(b.MinX),Math.Abs(b.MaxX)),
            Math.Max(Math.Max(Math.Abs(b.MinY),Math.Abs(b.MaxY)),Math.Max(Math.Abs(b.MinZ),Math.Abs(b.MaxZ))));
        private static bool MatrixFinite(DecalWorkMatrix m)
        {
            for(int r=0;r<4;r++) for(int c=0;c<4;c++) if(!Finite(m[r,c]) || Math.Abs(m[r,c])>1e18) return false;
            return true;
        }
        private static bool Perspective(DecalWorkMatrix p)
        {
            if(!MatrixFinite(p)) return false;
            // Conventional Unity perspective, including asymmetric/jittered FOV.
            // Oblique, orthographic, degenerate and custom homogeneous forms open.
            return p.M00>=0.0001 && p.M00<=10000 && p.M11>=0.0001 && p.M11<=10000 &&
                Math.Abs(p.M02)<=100 && Math.Abs(p.M12)<=100 && p.M01==0 && p.M03==0 && p.M10==0 && p.M13==0 &&
                p.M20==0 && p.M21==0 && p.M30==0 && p.M31==0 && p.M32==-1 && p.M33==0 && p.M23!=0 &&
                Math.Abs(p.M22)<=10000 && Math.Abs(p.M23)<=1e12;
        }
        private static bool Affine(DecalWorkMatrix m)
        {
            if(!MatrixFinite(m) || m.M30!=0 || m.M31!=0 || m.M32!=0 || m.M33!=1) return false;
            double a=m.M00*m.M00+m.M01*m.M01+m.M02*m.M02;
            double b=m.M10*m.M10+m.M11*m.M11+m.M12*m.M12;
            double c=m.M20*m.M20+m.M21*m.M21+m.M22*m.M22;
            double largest=Math.Max(a,Math.Max(b,c)), smallest=Math.Min(a,Math.Min(b,c));
            if(smallest<1e-20 || largest>1e20 || largest>smallest*1e12) return false;
            double det=m.M00*(m.M11*m.M22-m.M12*m.M21)-m.M01*(m.M10*m.M22-m.M12*m.M20)+m.M02*(m.M10*m.M21-m.M11*m.M20);
            return Math.Abs(det)>1e-6*Math.Pow(largest,1.5);
        }
        private static bool OutsideProjection(DecalWorkBounds b, DecalWorkMatrix p, DecalWorkMatrix camera, DecalWorkMatrix local)
        {
            // A box is outside only if its maximum support is strictly outside
            // one entire x/w or y/w half-space, including the outward allowance.
            // No depth planes, center test or perspective division.
            bool outside=false;
            for(int plane=0;plane<4;plane++)
            {
                int axis=plane/2; double sign=(plane&1)==0 ? 1 : -1;
                double support=0, magnitude=0;
                for(int i=0;i<4;i++)
                {
                    double coefficient=0, absoluteProducts=0;
                    for(int k=0;k<4;k++)
                    {
                        double pk=p[3,k]+sign*p[axis,k];
                        double pa=Math.Abs(p[3,k])+Math.Abs(p[axis,k]);
                        for(int j=0;j<4;j++)
                        {
                            double product=camera[k,j]*local[j,i];
                            coefficient+=pk*product;
                            absoluteProducts+=pa*Math.Abs(camera[k,j])*Math.Abs(local[j,i]);
                        }
                    }
                    double lo=i==0?b.MinX:i==1?b.MinY:i==2?b.MinZ:1;
                    double hi=i==0?b.MaxX:i==1?b.MaxY:i==2?b.MaxZ:1;
                    support+=coefficient*(coefficient>=0?hi:lo);
                    // Use the largest coordinate on ALL spatial axes for the
                    // error budget. A nominal zero matrix coefficient must not
                    // hide float TRS/quaternion construction error leaking from
                    // another large coordinate. Support still uses actual axes.
                    magnitude+=absoluteProducts*(i<3?Math.Max(1,MaxCoordinate(b)):1);
                }
                if(!Finite(support) || !Finite(magnitude) || magnitude>1e30) return false;
                if(support < -RelativeOutwardAllowance*Math.Max(1,magnitude)) outside=true;
            }
            // Validate all lateral axes before trusting any rejection: native
            // arithmetic on another axis must not overflow/turn unknown.
            return outside;
        }
    }
}
