using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool test,string message){if(!test)throw new Exception(message);checks++;}
    static GlassShapeFloat3 V(float x,float y,float z)=>new GlassShapeFloat3(x,y,z);
    static GlassShapeGrouping Run(GlassShapeFloat3[] positions,bool axes=false,int padded=12)
    {
        Check(GlassShapeContactCanonicalizer.TryCreate(positions,out var policy),"source policy");
        Check(GlassShapeGrouping.TryCreate(positions,positions.Length/3,padded,axes,policy,out var result,out var reason),"grouping: "+reason);
        return result;
    }
    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        // Coplanar triangles have different areas and ordering: normal magnitudes
        // cannot be used as keys, and opposite orientations must remain separate.
        var flat=new[]{V(0,0,0),V(2,0,0),V(0,1,0),V(0,0,0),V(0,1,0),V(2,0,0),V(2,0,0),V(2,2,0),V(0,1,0)};
        var grouped=Run(flat);
        Check(grouped.GroupCount==2,"oriented normalized plane groups");
        Check(grouped.TriangleFaceBits[0]==grouped.TriangleFaceBits[2]&&grouped.TriangleFaceBits[0]!=grouped.TriangleFaceBits[1],"actual triangle indices preserved");
        for(int i=3;i<12;i++)Check(grouped.TriangleFaceBits[i]==0,"padding unlabelled");
        Synthetic();
        if(args.Length>0)Live(args[0]);
        Console.WriteLine($"PASS: {checks} catalog grouping checks; runtime/native acceptance unproven.");
    }
    static void Synthetic()
    {
        var cube=new List<GlassShapeFloat3>();
        for(int axis=0;axis<3;axis++)foreach(int sign in new[]{-1,1})
        {
            GlassShapeFloat3 P(float u,float v){var p=new float[3];p[axis]=sign;p[(axis+1)%3]=u;p[(axis+2)%3]=v;return V(p[0],p[1],p[2]);}
            Quad(cube,P(-1,-1),P(1,-1),P(1,1),P(-1,1));
        }
        Check(Run(cube.ToArray()).GroupCount==6,"synthetic cube six planes");
        var wedge=new List<GlassShapeFloat3>();
        var a=V(-1,-1,-1);var b=V(1,-1,-1);var c=V(-1,1,-1);
        var d=V(-1,-1,1);var e=V(1,-1,1);var f=V(-1,1,1);
        wedge.AddRange(new[]{a,c,b,d,e,f});Quad(wedge,a,b,e,d);Quad(wedge,b,c,f,e);Quad(wedge,c,a,d,f);
        Check(Run(wedge.ToArray()).GroupCount==5,"wedge all source planes");
        var wg=Run(wedge.ToArray(),true);Check(wg.GroupCount==4,"wedge exact axis-only subset");
        Check(wg.TriangleFaceBits[4]==0&&wg.TriangleFaceBits[5]==0,"sloped facets retain zero labels");
        var band=new List<GlassShapeFloat3>();
        var outer=new[]{V(-2,-2,0),V(2,-2,0),V(2,2,0),V(-2,2,0)};
        var inner=new[]{V(-1,-1,0),V(1,-1,0),V(1,1,0),V(-1,1,0)};
        for(int i=0;i<4;i++)Quad(band,outer[i],outer[(i+1)%4],inner[(i+1)%4],inner[i]);
        var ring=Run(band.ToArray(),true);Check(ring.GroupCount==1&&ring.GroupTriangles[0].Length==8,"annular band retains eight triangles");
        for(int i=0;i<8;i++)Check(ring.GroupTriangles[0][i]==i,"annular triangle identity preserved");
        // No triangle contains the origin, so no hull/AABB substitution filled the hole.
        double area=0;foreach(int t in ring.GroupTriangles[0]){var x=band[t*3];var y=band[t*3+1];var z=band[t*3+2];area+=Math.Abs((y.X-x.X)*(z.Y-x.Y)-(y.Y-x.Y)*(z.X-x.X))*.5;}
        Check(area==12,"annulus area excludes inner hole");
        var concave=new List<GlassShapeFloat3>();
        Quad(concave,V(0,0,0),V(2,0,0),V(2,1,0),V(0,1,0));
        Quad(concave,V(0,1,0),V(1,1,0),V(1,2,0),V(0,2,0));
        var concaveGroup=Run(concave.ToArray(),true);Check(concaveGroup.GroupCount==1&&concaveGroup.GroupTriangles[0].Length==4,"concave cap retains actual triangles and void");
        var warped=band.ToArray();for(int i=0;i<warped.Length;i++)if(i%3==0)warped[i]=V(warped[i].X,warped[i].Y,2.9802322e-8f);
        Check(Run(warped,true).GroupCount==1,"fixed source policy restores warped axis cap");
        var planes=new List<GlassShapeFloat3>();for(int i=0;i<9;i++)planes.AddRange(new[]{V(0,0,i),V(1,0,i),V(0,1,i)});
        Check(GlassShapeContactCanonicalizer.TryCreate(planes,out var policy),"many-plane source policy");
        Check(!GlassShapeGrouping.TryCreate(planes.ToArray(),9,12,false,policy,out var rejected,out var why)&&rejected==null&&why.Contains("eight"),">8 groups fail open without partial publication");
        var degenerate=new[]{V(0,0,0),V(1,0,0),V(2,0,0)};GlassShapeContactCanonicalizer.TryCreate(degenerate,out policy);
        Check(!GlassShapeGrouping.TryCreate(degenerate,1,12,false,policy,out rejected,out _),"degenerate source triangle fails open");
        var near=new[]{V(0,0,0),V(1,0,0),V(0,1,0),V(0,0,.000002f),V(1,0,.000002f),V(0,1,.000002f)};
        Check(Run(near,true).GroupCount==2,"different source classes retain distinct exact parallel planes");
        GlassShapeContactCanonicalizer.TryCreate(band,out var forwardPolicy);var reverse=band.ToArray();Array.Reverse(reverse);
        GlassShapeContactCanonicalizer.TryCreate(reverse,out var reversePolicy);
        Check(GlassShapeGrouping.TryCreate(band.ToArray(),8,12,true,reversePolicy,out var reversed,out _),"reversed source dictionary admission");
        for(int i=0;i<12;i++)Check(reversed.TriangleFaceBits[i]==ring.TriangleFaceBits[i],"deterministic labels across policy source order");
        var source=band.ToArray();
        foreach(int padded in new[]{0,7,8,13,int.MaxValue})Check(!GlassShapeGrouping.TryCreate(source,8,padded,true,forwardPolicy,out rejected,out _)&&rejected==null,"invalid padded range fails open");
        Check(!GlassShapeGrouping.TryCreate(source,7,12,true,forwardPolicy,out rejected,out _),"actual range mismatch fails open");
        Check(!GlassShapeGrouping.TryCreate(null,8,12,true,forwardPolicy,out rejected,out _),"null source fails open");
        source[0]=V(63,63,63);Check(!GlassShapeGrouping.TryCreate(source,8,12,true,forwardPolicy,out rejected,out _),"unknown source dictionary key fails open");
    }
    static void Quad(List<GlassShapeFloat3> list,GlassShapeFloat3 a,GlassShapeFloat3 b,GlassShapeFloat3 c,GlassShapeFloat3 d)
        =>list.AddRange(new[]{a,b,c,a,c,d});
    static void Live(string directory)
    {
        int[] ids={166,168,314,315,316,167,118,313,317,207,209};int[] expected={6,5,4,7,5,4,3,6,3,4,3};
        var rows=new Dictionary<int,string[]>();foreach(string line in File.ReadLines(Path.Combine(directory,"catalog.csv")))
        {var c=line.Split(',');if(int.TryParse(c[0],out int id))rows[id]=c;}
        var positions=new List<GlassShapeFloat3>();var shapePositions=new Dictionary<int,GlassShapeFloat3[]>();
        foreach(int id in ids)
        {
            var c=rows[id];int actual=int.Parse(c[3]),padded=int.Parse(c[4]);var bytes=File.ReadAllBytes(Path.Combine(directory,id+"-cpu.bin"));
            Check(bytes.Length==padded*124,"actual capture stride "+id);
            string gpu=Path.Combine(directory,id+"-gpu.bin");if(File.Exists(gpu))Check(Convert.ToHexString(bytes)==Convert.ToHexString(File.ReadAllBytes(gpu)),"complete GPU bytes match CPU "+id);
            for(int i=actual*124;i<bytes.Length;i++)Check(bytes[i]==0,"zero full-record padding "+id);
            var ps=new GlassShapeFloat3[actual];for(int i=0;i<actual;i++)ps[i]=V(BitConverter.ToSingle(bytes,i*124),BitConverter.ToSingle(bytes,i*124+4),BitConverter.ToSingle(bytes,i*124+8));
            shapePositions.Add(id,ps);positions.AddRange(ps);
        }
        Check(GlassShapeContactCanonicalizer.TryCreate(positions,out var policy),"all11 bounded shared source policy");
        for(int j=0;j<ids.Length;j++)
        {
            int id=ids[j];var c=rows[id];int actual=int.Parse(c[3])/3,padded=int.Parse(c[4])/3;
            Check(GlassShapeGrouping.TryCreate(shapePositions[id],actual,padded,j>=5,policy,out var result,out var why),"actual grouping "+id+" "+why);
            Check(result.GroupCount==expected[j],"actual group count "+id+": "+result.GroupCount);
            uint used=0;for(int i=0;i<padded;i++){uint bit=result.TriangleFaceBits[i];used|=bit;Check((bit&(bit-1))==0,"single group bit "+id);if(i>=actual)Check(bit==0,"actual padding zero label "+id);}
            Check(used==(1u<<expected[j])-1,"dense labels "+id);
            int labelled=0;foreach(var group in result.GroupTriangles)foreach(int t in group){Check(t>=0&&t<actual,"actual local triangle index "+id);labelled++;}
            Console.WriteLine($"Actual capture {id}: {actual} actual/{padded} padded triangles; {result.GroupCount} groups; {labelled} labelled triangles.");
        }
    }
}
