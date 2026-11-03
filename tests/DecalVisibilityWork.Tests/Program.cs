using System;
using GraphicalTweaks;
static class Program
{
    static int checks, rejected;
    static readonly DecalWorkMatrix I=DecalWorkMatrix.Identity;
    static DecalWorkMatrix P(double f=1,double offset=0) => new DecalWorkMatrix { M00=f,M11=f,M02=offset,M22=-1.0001,M23=-0.2,M32=-1 };
    static void Assert(bool ok,string message) { checks++; if(!ok) throw new Exception("FAIL: "+message); }
    static bool Outside(DecalWorkBounds b,DecalWorkMatrix p,DecalWorkMatrix local=default,bool far=false,DecalWorkMatrix fp=default)
        => DecalVisibilityWorkModel.IsOutside(b,p,far,fp,I,local.M33==0?I:local);
    static void Main()
    {
        SnapshotTests();
        var box=new DecalWorkBounds(30,-1,-11,32,1,-9);
        Assert(Outside(box,P()),"known wholly lateral outside box");
        Assert(!Outside(new DecalWorkBounds(-1,-1,-11,1,1,-9),P()),"visible box keeps work");
        Assert(!Outside(new DecalWorkBounds(9,-1,-11,11,1,-9),P()),"edge intersection keeps work");
        Assert(!Outside(new DecalWorkBounds(10.00001,-.001,-10,10.00002,.001,-10),P()),"outer allowance keeps almost touching box");
        Assert(Outside(new DecalWorkBounds(10.01,-.001,-10,10.02,.001,-10),P()),"beyond outward margin can skip");
        Assert(!Outside(new DecalWorkBounds(-1,-1,-1,1,1,1),P()),"camera/near-plane crossing keeps work without division");
        Assert(!Outside(new DecalWorkBounds(-1,-1,-.3,1,1,-.01),P()),"near-depth crossing keeps work");
        Assert(!Outside(new DecalWorkBounds(0,0,-1e9,.001,.001,-1e9),P()),"no far-depth rejection");
        Assert(!Outside(box,P(1),far:true,fp:P(.2)),"far projection wider than near retains box");
        Assert(Outside(box,P(1),far:true,fp:P(2)),"both camera projections prove outside");
        Assert(!Outside(new DecalWorkBounds(-1,-1,-10,1,1,-9),P(1,1.1)),"asymmetric perspective accepted conservatively");
        var bad=P(); bad.M00=double.NaN;
        Assert(!Outside(box,bad),"NaN projection opens");
        Assert(!Outside(box,I),"orthographic projection opens");
        Assert(!Outside(box,P(),far:true,fp:bad),"unknown far projection opens even if near rejects");
        bad=P();bad.M32=0;
        Assert(!Outside(box,bad),"unsupported homogeneous projection opens");
        bad=I;bad.M00=1e-12;
        Assert(!Outside(box,P(),bad),"ill-conditioned local matrix opens");
        bad=I;bad.M03=double.PositiveInfinity;
        Assert(!Outside(box,P(),bad),"nonfinite view opens");
        bad=P();bad.M22=1e18;
        Assert(!Outside(box,bad),"unsupported depth arithmetic opens without using depth for rejection");
        Assert(!Outside(new DecalWorkBounds(double.NaN,0,0,1,1,1),P()),"invalid box opens");
        Assert(!Outside(new DecalWorkBounds(1,0,0,-1,1,1),P()),"inverted box opens");
        Assert(!Outside(new DecalWorkBounds(1e35,0,-10,2e35,1,-9),P()),"huge unsupported box opens");
        var cancelCamera=I;cancelCamera.M03=-1e16;
        var cancelLocal=I;cancelLocal.M03=1e16;
        Assert(!DecalVisibilityWorkModel.IsOutside(box,P(),false,default,cancelCamera,cancelLocal),"large origin cancellation allowance opens");
        RandomOracle(); EnvelopeTests(); WakeupTests();
        Console.WriteLine($"PASS: {checks} decal-work checks; {rejected} random groups conservatively outside");
    }
    static void SnapshotTests()
    {
        var live=new DecalWorkTransform { X=30,Y=0,Z=-10,QW=1,Scale=1 };
        var raw=live;raw.X=0;
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(live,raw,1,true,true),"uploaded transform inside/live outside must fail open");
        Assert(DecalVisibilityWorkModel.SameUploadedTransform(live,live,1,true,true),"exact valid uploaded/live match permits geometry proof");
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(live,live,1,false,true),"stale upload fails open");
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(live,live,1,true,false),"unknown upload length/index fails open");
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(live,live,0,true,true),"invalid uploaded raw slot fails open");
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(live,live,2,true,true),"unknown raw validity value fails open");
        raw=live;raw.QY=.001;
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(live,raw,1,true,true),"different rotation fails open");
        raw=live;raw.Scale=2;
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(live,raw,1,true,true),"different scale fails open");
        raw=live;raw.X=double.NaN;
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(live,raw,1,true,true),"nonfinite uploaded position fails open");
        raw=live;raw.Scale=double.PositiveInfinity;
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(raw,raw,1,true,true),"identical infinite TRS fails open");
        raw=live;raw.QW=0;
        Assert(!DecalVisibilityWorkModel.SameUploadedTransform(raw,raw,1,true,true),"identical degenerate quaternion fails open");
        var state=new DecalWorkState();bool woke;
        state.Decide(true,true,false,true,true,true,out woke);
        bool proof=DecalVisibilityWorkModel.SameUploadedTransform(live,new DecalWorkTransform { Z=-10,QW=1,Scale=1 },1,true,true);
        Assert(state.Decide(true,true,false,false,false,proof,out woke)&&woke&&!state.Skipped,"divergent GPU snapshot wakes prior skip below cadence");
    }
    static double Next(Random r,double a,double b) => a+r.NextDouble()*(b-a);
    static DecalWorkMatrix Pose(Random r,double translationScale)
    {
        double angle=Next(r,-Math.PI,Math.PI), c=(float)Math.Cos(angle),s=(float)Math.Sin(angle), scale=(float)Math.Pow(10,Next(r,-3,3));
        return new DecalWorkMatrix { M00=(float)(c*scale),M02=(float)(s*scale),M11=scale,M20=(float)(-s*scale),M22=(float)(c*scale),
            M03=(float)Next(r,-translationScale,translationScale),M13=(float)Next(r,-translationScale,translationScale),M23=(float)Next(r,-translationScale,translationScale),M33=1 };
    }
    static void RandomOracle()
    {
        var random=new Random(17631);
        for(int n=0;n<24000;n++)
        {
            double size=Math.Pow(10,Next(random,-6,5)), x=(float)Next(random,-1e5,1e5), y=(float)Next(random,-1e5,1e5),z=(float)Next(random,-1e5,1e5);
            var b=new DecalWorkBounds(x,y,z,x+size,y+size*Next(random,0,10),z+size*Next(random,0,10));
            var camera=Pose(random,n%3==0?1e12:1e5); var local=Pose(random,n%3==0?1e12:1e5);
            var p=P((float)Math.Pow(10,Next(random,-1,1)),(float)Next(random,-.5,.5));
            var fp=P((float)Math.Pow(10,Next(random,-1,1)),(float)Next(random,-.5,.5));
            bool outside=DecalVisibilityWorkModel.IsOutside(b,p,true,fp,camera,local);
            Assert(!outside || (CornerOracle(b,p,camera,local,false)&&CornerOracle(b,fp,camera,local,false)),"double corner oracle random "+n);
            Assert(!outside || (CornerOracle(b,p,camera,local,true)&&CornerOracle(b,fp,camera,local,true)),"native float association corner oracle random "+n);
            Assert(!outside || (CornerOracle(b,p,camera,local,true,true)&&CornerOracle(b,fp,camera,local,true,true)),"native float VP association oracle random "+n);
            if(outside) rejected++;
        }
        Assert(rejected>5000,"oracle test must exercise many true skips");
        // Dense boundary cloud includes negative-w and camera-crossing boxes.
        for(int n=0;n<4000;n++)
        {
            double d=Math.Pow(10,Next(random,-6,8)), delta=Next(random,-.002,.002)*d;
            var b=new DecalWorkBounds(d+delta,-d*.001,-d*1.00001,d+delta+d*.00001,d*.001,-d*.99999);
            bool outside=Outside(b,P());
            Assert(!outside || CornerOracle(b,P(),I,I,true),"margin corner oracle "+n);
        }
    }
    static double[] Multiply(DecalWorkMatrix m,double[] v,bool single)
    {
        var result=new double[4];
        for(int r=0;r<4;r++)
        {
            if(single)
            {
                float total=0;
                for(int c=0;c<4;c++) total=(float)(total+(float)((float)m[r,c]*(float)v[c]));
                result[r]=total;
            }
            else for(int c=0;c<4;c++) result[r]+=m[r,c]*v[c];
        }
        return result;
    }
    static double[] NativeVP(DecalWorkMatrix p,DecalWorkMatrix c,double[] v)
    {
        var result=new double[4];
        for(int r=0;r<4;r++)
        {
            float sum=0;
            for(int col=0;col<4;col++)
            {
                float pc=0;
                for(int k=0;k<4;k++) pc=(float)(pc+(float)((float)p[r,k]*(float)c[k,col]));
                sum=(float)(sum+(float)(pc*(float)v[col]));
            }
            result[r]=sum;
        }
        return result;
    }
    static bool CornerOracle(DecalWorkBounds b,DecalWorkMatrix p,DecalWorkMatrix camera,DecalWorkMatrix local,bool single,bool nativeVP=false)
    {
        double[] max={double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity};
        for(int corner=0;corner<8;corner++)
        {
            var v=new[]{(corner&1)==0?b.MinX:b.MaxX,(corner&2)==0?b.MinY:b.MaxY,(corner&4)==0?b.MinZ:b.MaxZ,1d};
            v=nativeVP?NativeVP(p,camera,Multiply(local,v,true)):Multiply(p,Multiply(camera,Multiply(local,v,single),single),single);
            max[0]=Math.Max(max[0],v[3]+v[0]);max[1]=Math.Max(max[1],v[3]-v[0]);
            max[2]=Math.Max(max[2],v[3]+v[1]);max[3]=Math.Max(max[3],v[3]-v[1]);
        }
        return max[0]<0||max[1]<0||max[2]<0||max[3]<0;
    }
    // Matches native Aabb.Size, Center and SetSize float operation order.
    static DecalWorkBounds NativeScale(DecalWorkBounds b,float scale)
    {
        float cx=((float)b.MaxX+(float)b.MinX)*.5f,cy=((float)b.MaxY+(float)b.MinY)*.5f,cz=((float)b.MaxZ+(float)b.MinZ)*.5f;
        float sx=((float)b.MaxX-(float)b.MinX)*scale*.5f,sy=((float)b.MaxY-(float)b.MinY)*scale*.5f,sz=((float)b.MaxZ-(float)b.MinZ)*scale*.5f;
        return new DecalWorkBounds(cx-sx,cy-sy,cz-sz,cx+sx,cy+sy,cz+sz);
    }
    static bool Contains(DecalWorkBounds outer,DecalWorkBounds inner) => outer.MinX<=inner.MinX&&outer.MinY<=inner.MinY&&outer.MinZ<=inner.MinZ&&outer.MaxX>=inner.MaxX&&outer.MaxY>=inner.MaxY&&outer.MaxZ>=inner.MaxZ;
    static void EnvelopeTests()
    {
        var envelope=new DecalWorkEnvelope();
        Assert(!envelope.Usable,"empty aggregate opens");
        var random=new Random(91);
        for(int n=0;n<300;n++)
        {
            float x=(float)Next(random,-1e7,1e7),y=(float)Next(random,-1e7,1e7),z=(float)Next(random,-1e7,1e7),s=(float)Math.Pow(10,Next(random,-5,6));
            var original=new DecalWorkBounds(x,y,z,x+s,y+s*.01f,z+s*.3f);
            var maximum=NativeScale(original,10);
            envelope.Include(original,maximum);
            Assert(Contains(envelope.Bounds,original)&&Contains(envelope.Bounds,maximum),"original and native max union");
            for(int eighth=8;eighth<=80;eighth++) Assert(Contains(envelope.Bounds,NativeScale(original,eighth/8f)),"every adaptive eighth within monotonic envelope");
        }
        var previous=envelope.Bounds;
        var replacement=new DecalWorkBounds(1e9,-1e9,-10,1e9+1,-1e9+1,-9);
        envelope.Include(replacement,NativeScale(replacement,10));
        Assert(Contains(envelope.Bounds,previous)&&Contains(envelope.Bounds,replacement),"replacement/moved ID grows without losing old space");
        Assert(Contains(envelope.Bounds,previous),"removal retains old conservative space without a shrink call");
        envelope.Include(new DecalWorkBounds(double.NaN,0,0,1,1,1),default);
        Assert(!envelope.Usable,"invalid original poisons aggregate");
        envelope.Include(replacement,NativeScale(replacement,10));
        Assert(!envelope.Usable,"later valid replacement cannot clear unknown historical state");
        var overflow=new DecalWorkBounds(float.MaxValue/2,0,0,float.MaxValue,1,1);
        var other=new DecalWorkEnvelope(); other.Include(overflow,NativeScale(overflow,10));
        Assert(!other.Usable,"native max expansion overflow opens");
    }
    static void WakeupTests()
    {
        var state=new DecalWorkState();bool woke;
        Assert(!state.Skipped,"new group starts active");
        Assert(!state.Decide(true,true,false,true,true,true,out woke)&&state.Skipped&&!woke,"scheduled outside group skips");
        Assert(!state.Decide(true,true,false,false,false,true,out woke)&&state.Skipped,"outside stays asleep below cadence");
        var b=new DecalWorkBounds(30,-1,-11,32,1,-9);var local=I;local.M03=-30;
        Assert(!Outside(b,P(),local),"moving entity enters with stationary camera");
        Assert(state.Decide(true,true,false,false,true,Outside(b,P(),local),out woke)&&woke&&!state.Skipped,"moving group wakes before cadence");
        state.Decide(true,true,false,true,true,true,out woke);
        Assert(state.Decide(true,true,false,false,false,false,out woke)&&woke,"unknown projection/view wakes skipped state");
        state.Decide(true,true,false,true,true,true,out woke);
        Assert(state.Decide(false,true,false,false,false,true,out woke)&&!state.Skipped,"disable refreshes stale proxy even without scan");
        state.Decide(true,true,false,true,true,true,out woke);
        Assert(state.Decide(true,false,true,false,false,true,out woke)&&!state.Skipped,"stock reset/full request always refreshes");
        Assert(state.Decide(true,true,true,false,false,true,out woke)&&!state.Skipped,"full higher multiplier request bypasses skip");
        Assert(!state.Decide(false,true,false,false,true,false,out woke),"disabled clean group retains baseline cadence");
        Assert(state.Decide(false,true,false,true,true,false,out woke),"disabled moved scan retains baseline work");
    }
}
