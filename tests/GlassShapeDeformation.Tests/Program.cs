using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value,string message) { if(!value) throw new Exception(message); checks++; }
    static GlassShapeFloat3 V(float x,float y,float z)=>new GlassShapeFloat3(x,y,z);
    static readonly float[] IdentityValues={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
    static readonly GlassShapeRotation Identity=new GlassShapeRotation(IdentityValues);
    static GlassShapeDeformationMaterial M(int mode=0,float unit=1,float low=-.001f,float high=.001f,int vertex=0,int alpha=2,int border=-1,int offset=0)
        =>new GlassShapeDeformationMaterial(mode,vertex,alpha,unit,low,high,border,offset);
    static GlassShapeDeformationInput I(GlassShapeFloat3 p,GlassShapeDeformationMaterial? m=null,int x=0,int y=0,int z=0,float unit=1,
        GlassShapeRotation? r=null,GlassShapeFloat3? center=null,GlassShapeFloat3? size=null,GlassShapeFloat3? position=null,GlassShapeFloat3? normal=null)
        =>new GlassShapeDeformationInput(p,normal??V(0,1,0),center??V(0,0,0),size??V(1,1,1),m??M(),x,y,z,unit,r??Identity,position??V(0,0,0));
    static GlassShapeFloat3 Run(GlassShapeDeformationInput i) { Check(GlassShapeDeformationModel.TryDeform(i,out var p),"admitted input rejected");return p; }
    static void Eq(GlassShapeFloat3 actual,GlassShapeFloat3 expected,string name)
        =>Check(actual.X==expected.X&&actual.Y==expected.Y&&actual.Z==expected.Z,name+": expected "+S(expected)+", observed "+S(actual));
    static string S(GlassShapeFloat3 p)=>$"({p.X:R},{p.Y:R},{p.Z:R})";
    static void Reject(GlassShapeDeformationInput i,string name)
    {Check(!GlassShapeDeformationModel.TryDeform(i,out var p),name);Eq(p,default(GlassShapeFloat3),name+" clears output");}

    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        // Break caught: rejecting supported fixed free linkage or applying brick translation before unit scaling.
        Eq(Run(I(V(-.5f,0,.5f),x:2,y:3,z:4,unit:.25f,position:V(10,-2,3))),V(9.875f,-1.625f,4.125f),"free resize then unit then rotation then position");
        Thresholds();Radial();Validation();
        if(args.Length>0) Live(args[0],args.Length>1?args[1]:null);
        Console.WriteLine($"PASS: {checks} deformation checks; CPU tests alone do not establish runtime/native equivalence.");
    }

    static void Thresholds()
    {
        // Break caught: using >= instead of strict > or measuring thresholds in source rather than bounds-relative space.
        foreach(float low in new[]{-.001f,0f})
        {
            float high=low==0?0:.001f;
            float[] samples={MathF.BitDecrement(low),low,MathF.BitIncrement(low),high,MathF.BitIncrement(high)};
            float[] shifts=low==high?new[]{0f,0,1,0,1}:new[]{0f,0,.5f,.5f,1};
            for(int i=0;i<samples.Length;i++)
                Eq(Run(I(V(samples[i],samples[i],samples[i]),M(low:low,high:high),x:2,y:4,z:6)),
                    V(samples[i]+2*shifts[i],samples[i]+4*shifts[i],samples[i]+6*shifts[i]),"strict threshold "+i+" "+low);
        }
        Eq(Run(I(V(4,6,8),x:2,y:4,z:6,center:V(4,6,8))),V(5,8,11),"bounds center subtraction precedes thresholds");
        foreach(int n in new[]{0,15}) foreach(float materialUnit in new[]{.25f,1,16}) foreach(float brickUnit in new[]{.125f,1,8})
            Eq(Run(I(V(-.5f,0,.5f),M(unit:materialUnit),x:n,y:n,z:n,unit:brickUnit)),
                V(-.5f*brickUnit,(materialUnit*n*.5f)*brickUnit,(.5f+materialUnit*n)*brickUnit),"unit and count extremes");
        Eq(Run(I(V(1,2,3),r:new GlassShapeRotation(new float[]{0,0,1,0,0,1,0,0,-1,0,0,0,0,0,0,1}),unit:2,position:V(7,11,13))),V(13,15,11),"logical row-major multiplication orientation");
    }

    static void Radial()
    {
        // Break caught: treating radial counts as resizeUnit-scaled or allowing countY to change the radius.
        Eq(Run(I(V(3,4,.5f),M(7,unit:16),x:5,y:15,z:6,center:V(.5f,.5f,.5f))),V(6,8,.5f),"radiusXY strict axial boundary and ignores unit/countY");
        float above=MathF.BitIncrement(.5f);
        Eq(Run(I(V(3,4,above),M(7),x:5,z:6,center:V(.5f,.5f,.5f))),V(6,8,above+6),"radiusXY z strictly above .5");
        Eq(Run(I(V(1,2,2),M(8,unit:.25f),x:3,y:15,z:15,center:V(.5f,.5f,.5f))),V(2,4,4),"radiusXYZ ignores countY/Z and material unit");
        foreach(int mode in new[]{7,8})
        {
            Eq(Run(I(V(0,0,0),M(mode),x:15,y:15,z:15,center:V(.5f,.5f,.5f))),V(0,0,0),"radial center does not divide by zero");
            Eq(Run(I(V(.01f,0,0),M(mode),x:15,center:V(.5f,.5f,.5f))),V(.01f,0,0),"radius exactly .01 remains fixed");
            float over=MathF.BitIncrement(.01f);
            Check(Run(I(V(over,0,0),M(mode),x:1,center:V(.5f,.5f,.5f))).X>1,"radius immediately above .01 expands");
            Eq(Run(I(V(3,4,0),M(mode),x:0,center:V(2,3,4),size:V(4,6,8))),V(3,4,0),"nontrivial anchor zero counts preserves position");
        }
    }

    static void Validation()
    {
        foreach(int mode in new[]{-1,1,2,3,4,5,6,9,99})Reject(I(V(0,0,0),M(mode)),"unsupported resize "+mode);
        foreach(int mode in new[]{-1,1,2,3,4,5,6,7,8,99})Reject(I(V(0,0,0),M(vertex:mode)),"vertex modes including unmodeled offset3 "+mode);
        foreach(int alpha in new[]{0,1,3,10,20,99})Reject(I(V(0,0,0),M(alpha:alpha)),"non-glass alpha "+alpha);
        Reject(I(V(0,0,0),M(border:0)),"bordered material");Reject(I(V(0,0,0),M(offset:1)),"building offset");
        foreach(int bad in new[]{-1,16,int.MaxValue})
        {Reject(I(V(0,0,0),x:bad),"countX range");Reject(I(V(0,0,0),y:bad),"countY range");Reject(I(V(0,0,0),z:bad),"countZ range");}
        foreach(float bad in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
        {
            foreach(var p in new[]{V(bad,0,0),V(0,bad,0),V(0,0,bad)})
            {Reject(I(p),"nonfinite position");Reject(I(V(0,0,0),normal:p),"nonfinite normal");Reject(I(V(0,0,0),center:p),"nonfinite center");Reject(I(V(0,0,0),size:p),"nonfinite size");Reject(I(V(0,0,0),position:p),"nonfinite brick position");}
            Reject(I(V(0,0,0),unit:bad),"nonfinite brick unit");Reject(I(V(0,0,0),M(unit:bad)),"nonfinite resize unit");
            Reject(I(V(0,0,0),M(low:bad)),"nonfinite low");Reject(I(V(0,0,0),M(high:bad)),"nonfinite high");
            for(int j=0;j<16;j++){var a=(float[])IdentityValues.Clone();a[j]=bad;Reject(I(V(0,0,0),r:new GlassShapeRotation(a)),"nonfinite rotation coefficient "+j);}
        }
        foreach(float bad in new[]{0f,-1f}){Reject(I(V(0,0,0),unit:bad),"invalid brick unit");Reject(I(V(0,0,0),M(unit:bad)),"invalid resize unit");}
        Reject(I(V(0,0,0),M(low:2,high:1)),"reversed thresholds");Reject(I(V(0,0,0),size:V(-1,1,1)),"negative bounds size");
        Reject(I(V(0,0,0),size:V(0,1,1)),"zero bounds extent");Reject(I(V(0,0,0),r:default(GlassShapeRotation)),"zero rotation");
        foreach(int coefficient in new[]{0,1,3,12,15}){var a=(float[])IdentityValues.Clone();a[coefficient]+=1;Reject(I(V(0,0,0),r:new GlassShapeRotation(a)),"scale/shear/projective matrix");}
        var reflected=(float[])IdentityValues.Clone();reflected[0]=-1;Reject(I(V(0,0,0),r:new GlassShapeRotation(reflected)),"improper reflection");
        Reject(I(V(float.MaxValue,0,0),unit:2),"unit multiplication overflow");
        Reject(I(V(float.MaxValue,0,0),center:V(-float.MaxValue,0,0)),"relative subtraction overflow");
        Reject(I(V(1,0,0),M(unit:float.MaxValue),x:15),"extension overflow");
        Reject(I(V(1e30f,0,0),M(8)),"radial length overflow");
        for(uint index=0;index<32;index++)Check(GlassShapeDeformationModel.DecodeRotationIndex(0xffffffe0u|index)==index,"packed rotation masks low5bits");
        var mutable=(float[])IdentityValues.Clone();var snapshot=new GlassShapeRotation(mutable);mutable[0]=0;
        Eq(Run(I(V(1,2,3),r:snapshot)),V(1,2,3),"rotation constructor takes immutable value snapshot");
    }

    static void Live(string directory,string fixturePath)
    {
        var matrices=new float[64][];for(int i=0;i<64;i++)matrices[i]=new float[16];
        foreach(string line in File.ReadLines(Path.Combine(directory,"rotations.csv")))
        {var c=line.Split(',');if(c[0]=="rotation")continue;matrices[int.Parse(c[0])][4*int.Parse(c[1])+int.Parse(c[2])]=float.Parse(c[3]);}
        int residuals=0;
        for(uint i=0;i<32;i++)
        {
            var a=matrices[GlassShapeDeformationModel.DecodeRotationIndex(0xff00u|i)];var rotation=new GlassShapeRotation(a);
            var p=Run(I(V(1,0,0),r:rotation));Eq(p,V(a[0],a[4],a[8]),"live matrix X basis "+i);
            Eq(Run(I(V(0,1,0),r:rotation)),V(a[1],a[5],a[9]),"live matrix Y basis "+i);
            Eq(Run(I(V(0,0,1),r:rotation)),V(a[2],a[6],a[10]),"live matrix Z basis "+i);
            foreach(float f in a)if(f!=0&&f!=1&&f!=-1)residuals++;
        }
        Check(residuals>0,"live matrices exercise unsnapped coefficients");
        Console.WriteLine($"Live input: 32 selectable matrix indices; {residuals} non-cardinal coefficients preserved.");
        ExportLive(directory,matrices,fixturePath);
    }

    static GlassShapeFloat3 Read3(byte[] bytes,int offset)=>V(BitConverter.ToSingle(bytes,offset),BitConverter.ToSingle(bytes,offset+4),BitConverter.ToSingle(bytes,offset+8));
    static void Write3(BinaryWriter writer,GlassShapeFloat3 p){writer.Write(p.X);writer.Write(p.Y);writer.Write(p.Z);}
    static void ExportLive(string directory,float[][] matrices,string fixturePath)
    {
        // Only captured eligible clear-family shapes; declaration max counts remain the integration owner's responsibility.
        var selected=new HashSet<int>(new[]{166,168,314,315,316,167,118,313,317,207,209});
        var materials=new Dictionary<int,GlassShapeDeformationMaterial>();
        foreach(string line in File.ReadLines(Path.Combine(directory,"materials.csv")))
        {
            var c=line.Split(',');if(c[0]=="brickId"||!selected.Contains(int.Parse(c[0])))continue;
            if(c[3]!="transparent"||c[4]!="none"||c[5]!="-1"||c[12]!="off")continue;
            int mode=c[9]=="free"?0:c[9]=="radiusXY"?7:c[9]=="radiusXYZ"?8:-1;
            Check(mode>=0,"unknown linkage in selected live material");
            materials.Add(int.Parse(c[2]),M(mode,float.Parse(c[8]),float.Parse(c[10]),float.Parse(c[11])));
        }
        var shapes=new List<(int id,int count,byte[] bytes)>();
        foreach(string line in File.ReadLines(Path.Combine(directory,"catalog.csv")))
        {
            var c=line.Split(',');if(c[0]=="brickId"||!selected.Contains(int.Parse(c[0])))continue;
            int id=int.Parse(c[0]),count=int.Parse(c[3]);var source=File.ReadAllBytes(Path.Combine(directory,$"{id}-cpu.bin"));
            Check(source.Length==int.Parse(c[4])*124,"live complete padded source stride");
            Check(Convert.ToHexString(source)==Convert.ToHexString(File.ReadAllBytes(Path.Combine(directory,$"{id}-gpu.bin"))),"live source CPU/GPU capture identical");
            for(int i=count*124;i<source.Length;i++)Check(source[i]==0,"padding bytes remain zero");
            for(int i=0;i<count;i++)Check(materials.ContainsKey(BitConverter.ToInt32(source,i*124+60)),"used live material is fixed and admitted");
            shapes.Add((id,count,source));
        }
        Check(shapes.Count==11,"all eleven clear-family captures exercised");
        BinaryWriter writer=fixturePath==null?null:new BinaryWriter(File.Create(fixturePath));
        try
        {
            if(writer!=null)
            {
                writer.Write(0x47534431u);writer.Write(64);foreach(var row in matrices)foreach(float f in row)writer.Write(f);
                writer.Write(materials.Count);foreach(var pair in materials){writer.Write(pair.Key);writer.Write(pair.Value.ResizeMode);writer.Write(pair.Value.ResizeUnit);writer.Write(pair.Value.GeometryLow);writer.Write(pair.Value.GeometryHigh);}
                writer.Write(shapes.Count);foreach(var s in shapes){writer.Write(s.id);writer.Write(s.count);writer.Write(s.bytes,0,s.count*124);}
                writer.Write(shapes.Count*32*5);
            }
            int samples=0;
            foreach(var s in shapes)for(uint rotation=0;rotation<32;rotation++)for(int variation=0;variation<5;variation++)
            {
                var counts=variation==0?new[]{0,0,0}:variation==1?new[]{1,2,3}:variation==2?new[]{15,15,15}:variation==3?new[]{15,0,7}:new[]{4,15,2};
                float unit=variation==1?.25f:variation==3?.125f:variation==4?16:1;
                var position=variation==1?V(10,-2,3):variation==2?V(.125f,-.25f,.5f):variation==3?V(8192,-8192,4096):V(0,0,0);
                uint packed=0xff00u|rotation; // low five bits, independent of upper material bits.
                if(writer!=null){writer.Write(s.id);writer.Write(packed);writer.Write((uint)(counts[0]|counts[1]<<4|counts[2]<<8));writer.Write(unit);Write3(writer,position);}
                for(int i=0;i<s.count;i++)
                {
                    int at=i*124;var material=materials[BitConverter.ToInt32(s.bytes,at+60)];
                    var input=I(Read3(s.bytes,at),material,counts[0],counts[1],counts[2],unit,new GlassShapeRotation(matrices[GlassShapeDeformationModel.DecodeRotationIndex(packed)]),Read3(s.bytes,at+64),Read3(s.bytes,at+76),position,Read3(s.bytes,at+12));
                    var output=Run(input);if(writer!=null)Write3(writer,output);samples++;
                }
            }
            Console.WriteLine($"Live geometry: {shapes.Count} shapes, {materials.Count} used/declared fixed clear materials; {samples} vertex cases (5 count/unit/position combinations x32 indices). "+(writer==null?"No export requested.":"Ignored fixture exported."));
        }
        finally{writer?.Dispose();}
    }
}
