using System;
using GraphicalTweaks;

static partial class Program
{
    static readonly GlassShapeRotation Identity=new GlassShapeRotation(new float[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1});
    static GlassShapeDeformationInput Input(GlassShapeFloat3 source,GlassShapeRotation? rotation=null,GlassShapeFloat3? placement=null,
        GlassShapeDeformationMaterial? material=null,GlassShapeFloat3? center=null,GlassShapeFloat3? size=null,int x=0,int y=0,int z=0,float unit=1)
        =>new GlassShapeDeformationInput(source,V(0,1,0),center??default,size??V(1,1,1),material??new GlassShapeDeformationMaterial(0,0,2,1,-.001f,.001f),x,y,z,unit,rotation??Identity,placement??default);
    static void Synthetic()
    {
        foreach(var bad in new[]{float.NaN,float.PositiveInfinity,65f})Check(!GlassShapeContactCanonicalizer.TryCreate(new[]{V(bad,0,0)},out _),"invalid source catalog rejected");
        Check(!GlassShapeContactCanonicalizer.TryCreate(Array.Empty<GlassShapeFloat3>(),out _),"empty catalog rejected");
        Check(!GlassShapeContactCanonicalizer.TryCreate(new GlassShapeFloat3[65537],out _),"oversized catalog rejected");
        var samples=new[]{V(.5f,.5000008f,.5000016f),V(-0f,.5025f,0)};
        Check(GlassShapeContactCanonicalizer.TryCreate(samples,out var p),"diameter sample admitted");
        Check(p.TryCanonicalize(samples[0],out var mapped)&&mapped.X==.5f&&mapped.Y==.5f&&mapped.Z==samples[0].Z,"diameter avoids transitive snapping");
        Check(p.TryCanonicalize(samples[1],out mapped)&&mapped.Y==.5025f,"real .0025 profile retained");
        Check(!p.TryCanonicalize(V(.5000001f,0,0),out _),"unknown source coordinate never nearest-snapped");
        Check(GlassShapeContactCanonicalizer.TryCreate(new[]{samples[1],samples[0]},out var reverse)&&reverse.TryCanonicalize(samples[0],out var remapped)&&remapped.Y==.5f&&remapped.Z==samples[0].Z,"source order independence");
        Check(p.TryCanonicalizeRadialAnchor(V(-.5000001f,0,0),out mapped)&&mapped.X==-.5f,"derived radial anchor near known coordinate");
        Check(!p.TryCanonicalizeRadialAnchor(V(-.501f,0,0),out _),"unknown radial anchor rejected");
        float[] coefficients={1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        Check(GlassShapeContactModel.TryCanonicalRotation(Identity,out _),"identity admitted");
        foreach(int at in new[]{0,1,3,12,15})
        {
            var invalid=(float[])coefficients.Clone();invalid[at]+=1e-5f;
            Check(!GlassShapeContactModel.TryCanonicalRotation(new GlassShapeRotation(invalid),out _),"matrix outside measured bound rejected");
        }
        var reflected=(float[])coefficients.Clone();reflected[0]=-1;
        Check(!GlassShapeContactModel.TryCanonicalRotation(new GlassShapeRotation(reflected),out _),"reflection rejected");
        GlassShapeContactCanonicalizer.TryCreate(new[]{V(.5f,0,-.5f)},out p);
        var exact=new GlassPolygonPoint(8192.03125,-.03125,1048575);
        Check(GlassShapeContactModel.TryDeform(Input(V(.5f,0,-.5f),placement:V((float)exact.X,(float)exact.Y,(float)exact.Z),unit:.25f),p,exact,out var contact)&&contact.X==8192.15625&&contact.Y==-.03125&&contact.Z==1048574.875,"exact placement applied after local float math");
        Check(!GlassShapeContactModel.TryDeform(Input(V(.5f,0,-.5f)),p,new GlassPolygonPoint(.001,0,0),out _),"off-grid placement rejected");
        Check(!GlassShapeContactModel.TryDeform(Input(V(.5f,0,-.5f)),p,new GlassPolygonPoint(1,0,0),out _),"mismatched raw placement rejected");
        foreach(float low in new[]{.5f,.50000006f})
        {
            GlassShapeContactCanonicalizer.TryCreate(new[]{V(.5f,0,0),V(.5000001f,0,0)},out p);
            Check(!GlassShapeContactModel.TryDeform(Input(V(.5000001f,0,0),material:new GlassShapeDeformationMaterial(0,0,2,1,low,low),x:1),p,default,out _),"free threshold branch switch rejected");
        }
        GlassShapeContactCanonicalizer.TryCreate(new[]{V(.01f,0,0),V(MathF.BitIncrement(.01f),0,0)},out p);
        Check(!GlassShapeContactModel.TryDeform(Input(V(MathF.BitIncrement(.01f),0,0),material:new GlassShapeDeformationMaterial(8,0,2,1,0,0),center:V(.5f,.5f,.5f),x:1),p,default,out _),"radial .01 branch switch rejected");
        GlassShapeContactCanonicalizer.TryCreate(new[]{V(1,0,.5f),V(1,0,MathF.BitIncrement(.5f))},out p);
        Check(!GlassShapeContactModel.TryDeform(Input(V(1,0,MathF.BitIncrement(.5f)),material:new GlassShapeDeformationMaterial(7,0,2,1,0,0),center:V(.5f,.5f,.5f),z:1),p,default,out _),"radiusXY strict z branch switch rejected");
        GlassShapeContactCanonicalizer.TryCreate(new[]{V(0,0,0),V(.0000009f,0,0)},out p);
        Check(!GlassShapeContactModel.TryDeform(Input(V(.0000009f,0,0),material:new GlassShapeDeformationMaterial(8,0,2,1,0,0),center:V(.490001f,.5f,.5f),x:15),p,default,out _),"unknown derived anchor remains visible fallback");
        GlassShapeContactCanonicalizer.TryCreate(new[]{V(.0101f,.0000009f,0),V(0,0,0)},out p);
        Check(!GlassShapeContactModel.TryDeform(Input(V(.0101f,.0000009f,0),material:new GlassShapeDeformationMaterial(8,0,2,1,0,0),center:V(.5f,.5f,.5f),x:15),p,default,out _),"radial noise amplification beyond local error bound rejected");
        GlassShapeContactCanonicalizer.TryCreate(new[]{V(.5f,0,-.5f)},out p);
        Check(GlassShapeContactModel.TryDeform(Input(V(.5f,0,-.5f)),p,default,out _),"negative material fixtures use otherwise admitted source");
        foreach(var m in new[]{new GlassShapeDeformationMaterial(2,0,2,1,0,0),new GlassShapeDeformationMaterial(0,3,2,1,0,0),
            new GlassShapeDeformationMaterial(0,0,1,1,0,0),new GlassShapeDeformationMaterial(0,0,2,1,0,0,0),new GlassShapeDeformationMaterial(0,0,2,1,0,0,-1,1)})
        {
            Check(!GlassShapeContactModel.TryDeform(Input(V(.5f,0,-.5f),material:m),p,default,out contact)&&contact.X==0&&contact.Y==0&&contact.Z==0,"unsupported native material controls rejected with zero output");
        }
        // Exact coverage must retain a real small separation; no epsilon inside coverage.
        var a=new GlassPolygonTriangle(new GlassPolygonPoint(0,0,0),new GlassPolygonPoint(1,0,0),new GlassPolygonPoint(0,1,0));
        var b=new GlassPolygonTriangle(new GlassPolygonPoint(0,0,.0025),new GlassPolygonPoint(0,1,.0025),new GlassPolygonPoint(1,0,.0025));
        var coverage=new GlassPolygonCoverageModel();coverage.Upsert(new GlassPolygonCoverageBrick(1,1,255,new GlassPolygonFaceGroup(0,a)));
        coverage.Upsert(new GlassPolygonCoverageBrick(1,2,255,new GlassPolygonFaceGroup(0,b)));
        Check(coverage.GetMask(1)==0&&coverage.GetMask(2)==0,"real .0025 gap stays visible");
        var c=new GlassPolygonTriangle(new GlassPolygonPoint(.0025,0,0),new GlassPolygonPoint(.0025,1,0),new GlassPolygonPoint(1.0025,0,0));
        coverage.Upsert(new GlassPolygonCoverageBrick(1,2,255,new GlassPolygonFaceGroup(0,c)));
        Check(coverage.GetMask(1)==0&&coverage.GetMask(2)==0,"real .0025 tangent profile mismatch stays visible");
    }
}
