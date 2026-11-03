using System;
using GraphicalTweaks;

internal static class Program
{
    private static int checks;
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private static GlassShapeVariantSource Shape(int start=7,int padded=12,int groups=2,int actual=4)
    {
        var bits=new uint[padded];
        for(int i=0;i<Math.Min(actual,groups);i++)bits[i]=1u<<i;
        return new GlassShapeVariantSource(start,start,actual,padded,groups,bits);
    }
    private static bool Plan(GlassShapeVariantSource[] sources,out GlassShapeVariantLayout layout,long budget=10000000,int vertices=3003)
        =>GlassShapeVariantLayout.TryCreate(vertices,sources,budget,out layout);
    private static void Main()
    {
        var source=Shape();
        Check(Plan(new[]{source},out var layout),"valid irregularly aligned native shape must plan");
        Check(layout.SourceVertices==3003&&layout.OwnedVertices==3003+4*36,"all complete padded mask variants allocated");
        Check(layout.StaticBytes==(long)layout.OwnedVertices*124+1001*20,"all three static GPU buffers budgeted");
        Check(layout.TriangleFaceBits.Length==1001&&layout.Clusters.Length==1001,"native-triangle indexed tables");
        var d=layout.Clusters[7];
        Check(d.VariantBaseTriangle==1001&&d.SourceStartTriangle==7&&d.PaddedTriangles==12&&d.ValidMask==3,"descriptor units and nonaligned native base");
        Check(layout.Clusters[0].ValidMask==0&&layout.Clusters[8].ValidMask==0,"only cluster starts have descriptors");
        source.TriangleFaceBits[0]=0;
        Check(layout.TriangleFaceBits[7]==1,"layout snapshots mutable source labels");
        Check(Plan(new[]{Shape(7,24,2,16),Shape(55,12,1,2)},out layout),"mixed multicluster sources");
        Check(layout.Clusters[19].VariantBaseTriangle==1013&&layout.Clusters[19].SourceStartTriangle==7,"second cluster keeps whole shape identity and offset");
        Check(layout.Clusters[55].VariantBaseTriangle==1097,"next source follows whole variant extent");
        Check(layout.TriangleFaceBits[7]==1&&layout.TriangleFaceBits[8]==2&&layout.TriangleFaceBits[23]==0,"eligible bits and unmaskable surfaces/padding preserved");
        Check(Plan(new[]{Shape()},out layout),"budget baseline");
        long bytes=layout.StaticBytes;
        Check(Plan(new[]{Shape()},out _,bytes),"exact byte budget accepted");
        Check(!Plan(new[]{Shape()},out _,bytes-1),"one byte over budget rejected");
        Check(!Plan(new[]{Shape()},out _,long.MaxValue,int.MaxValue),"invalid nontriangular source rejected without overflow");
        Check(!Plan(new[]{Shape(0)},out _),"triangle zero inactive");
        Check(!Plan(new[]{Shape(999)},out _),"source extent must fit native pool");
        Check(!Plan(new[]{Shape(7,13)},out _),"clusters are twelve triangles");
        Check(!Plan(new[]{Shape(7,12,0)},out _),"zero face groups rejected");
        Check(!Plan(new[]{Shape(7,12,9,9)},out _),"more than 256 variants rejected");
        Check(!Plan(new[]{Shape(7,12,2,0)},out _),"empty source rejected");
        Check(!Plan(new[]{Shape(7,12,2,13)},out _),"actual triangles cannot exceed padded span");
        Check(!Plan(new[]{Shape(),Shape(18)},out _),"overlapping sources rejected");
        Check(!Plan(new[]{Shape(),Shape()},out _),"duplicate identity rejected");
        Check(!Plan(Array.Empty<GlassShapeVariantSource>(),out _),"empty catalogue is not a working remap");
        Check(!Plan(new GlassShapeVariantSource[]{null},out _),"null descriptor rejected");
        foreach(uint invalid in new uint[]{3,4,0x80000000})
        {
            source=Shape();source.TriangleFaceBits[0]=invalid;
            Check(!Plan(new[]{source},out _),"malformed or unknown triangle bit rejected");
        }
        source=Shape();source.TriangleFaceBits[4]=1;
        Check(!Plan(new[]{source},out _),"padding cannot be labelled as physical face");
        source=Shape();source.TriangleFaceBits[1]=0;
        Check(!Plan(new[]{source},out _),"declared group must exist in actual geometry");
        Check(Plan(new[]{Shape(7,12,8,8)},out layout),"eight groups supported");
        Check(layout.OwnedVertices==3003+256*36&&layout.Clusters[7].ValidMask==255,"max variants bounded");
        Console.WriteLine($"GlassShapeVariantLayout: {checks} checks passed.");
    }
}
