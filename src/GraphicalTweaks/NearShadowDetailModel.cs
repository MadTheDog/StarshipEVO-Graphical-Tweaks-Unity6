using System;
using System.Runtime.InteropServices;

namespace GraphicalTweaks
{
    public readonly struct NearShadowDetailSplits
    {
        public readonly float Two,FourX,FourY,FourZ;
        public NearShadowDetailSplits(float two,float x,float y,float z){Two=two;FourX=x;FourY=y;FourZ=z;}
        internal static bool Same(float a,float b)=>new FloatBits{Value=a}.Bits==new FloatBits{Value=b}.Bits;
        internal bool Same(NearShadowDetailSplits other)=>Same(Two,other.Two)&&Same(FourX,other.FourX)&&Same(FourY,other.FourY)&&Same(FourZ,other.FourZ);
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)]internal float Value;
            [FieldOffset(0)]internal int Bits;
        }
    }
    public sealed class NearShadowDetailModel
    {
        public int Choice {get;private set;}
        private static readonly int[] Ranges={0,15,30,60,100};
        public int? RequestedNearRange => Choice==0?(int?)null:Ranges[Choice];
        public bool TrySet(int choice){if(choice<0||choice>4)return false;Choice=choice;return true;}
        public void Reset(){Choice=0;}
        public static int NormalizeSaved(int choice)=>choice>=0&&choice<=4?choice:0;
        public bool TryCalculate(int cascades,float totalDistance,NearShadowDetailSplits native,out NearShadowDetailSplits target)
        {
            target=native;
            if(Choice==0||!Finite(totalDistance)||totalDistance<=0||(cascades!=2&&cascades!=4))return false;
            if(cascades==2&&!Finite(native.Two))return false;
            float upper=.95f;
            if(cascades==4)
            {
                if(!Finite(native.FourX)||!Finite(native.FourY)||!Finite(native.FourZ)||native.FourY<=0||native.FourZ<=native.FourY||native.FourZ>=1)return false;
                upper=Math.Min(upper,native.FourY-.005f);
                if(upper<.005f)return false;
            }
            float first=(float)Math.Max(.005,Math.Min(upper,(double)Ranges[Choice]/totalDistance));
            target=cascades==2?new NearShadowDetailSplits(first,native.FourX,native.FourY,native.FourZ):
                new NearShadowDetailSplits(native.Two,first,native.FourY,native.FourZ);
            return true;
        }
        private static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
    }
    internal sealed class NearShadowDetailOwnership
    {
        private bool captured,ownsTwo,ownsFour;
        private NearShadowDetailSplits last;
        internal NearShadowDetailSplits Baseline {get;private set;}
        internal bool HasOverride => ownsTwo||ownsFour;
        internal bool Evaluate(NearShadowDetailModel model,int cascades,float distance,NearShadowDetailSplits current,out NearShadowDetailSplits desired)
        {
            Observe(current);
            bool supported=model.TryCalculate(cascades,distance,Baseline,out var target);
            int mode=supported?cascades:0;
            desired=current;
            if(ownsTwo&&mode!=2){desired=new NearShadowDetailSplits(Baseline.Two,desired.FourX,desired.FourY,desired.FourZ);ownsTwo=false;}
            if(ownsFour&&mode!=4){desired=new NearShadowDetailSplits(desired.Two,Baseline.FourX,desired.FourY,desired.FourZ);ownsFour=false;}
            if(mode==2){desired=new NearShadowDetailSplits(target.Two,desired.FourX,desired.FourY,desired.FourZ);ownsTwo=true;}
            else if(mode==4){desired=new NearShadowDetailSplits(desired.Two,target.FourX,desired.FourY,desired.FourZ);ownsFour=true;}
            last=desired;
            if(!HasOverride)captured=false;
            return !current.Same(desired);
        }
        internal bool Restore(NearShadowDetailSplits current,out NearShadowDetailSplits desired)
        {
            desired=current;
            if(!HasOverride)return false;
            Observe(current);
            desired=new NearShadowDetailSplits(ownsTwo?Baseline.Two:current.Two,ownsFour?Baseline.FourX:current.FourX,current.FourY,current.FourZ);
            ownsTwo=ownsFour=captured=false;last=desired;
            return !current.Same(desired);
        }
        private void Observe(NearShadowDetailSplits current)
        {
            if(!captured){Baseline=current;captured=true;return;}
            // We own only Two or FourX. Unowned values always follow native.
            // Detect each component independently: changing Y must not turn our
            // previously written X into the game's new restoration baseline.
            float x=ownsFour&&NearShadowDetailSplits.Same(current.FourX,last.FourX)?Baseline.FourX:current.FourX;
            bool laterChanged=!NearShadowDetailSplits.Same(current.FourY,last.FourY)||!NearShadowDetailSplits.Same(current.FourZ,last.FourZ);
            if(ownsFour&&laterChanged&&!(x>=0&&x<current.FourY&&current.FourY<current.FourZ&&current.FourZ<=1))
                x=current.FourX; // Adopt the external vector if the old X would now break ordering.
            Baseline=new NearShadowDetailSplits(
                ownsTwo&&NearShadowDetailSplits.Same(current.Two,last.Two)?Baseline.Two:current.Two,
                x,
                current.FourY,current.FourZ);
        }
    }
}
