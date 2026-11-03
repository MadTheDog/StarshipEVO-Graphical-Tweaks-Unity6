using System;
using System.Collections.Generic;
using System.Linq;
using GraphicalTweaks;

static partial class Program
{
    static void ActualRoundedCornerCoverage(List<Shape> shapes,GlassShapeContactCanonicalizer policy,GlassShapeRotation[] rotations,
        Dictionary<int,GlassShapeDeformationMaterial> materials)
    {
        int[] zero={0,0,0};var corner=shapes.Single(s=>s.Id==209);var arch=shapes.Single(s=>s.Id==207);var cube=shapes.Single(s=>s.Id==166);
        var target=AxisBrick(corner,policy,Identity,default,zero,1,materials,1);
        var archNeighbour=AxisBrick(arch,policy,Identity,new GlassPolygonPoint(0,0,-1),zero,1,materials,2);
        ProfileMismatch(target.Groups.Single(g=>g.Bit==4),archNeighbour.Groups.Single(g=>g.Bit==5),2,"209-Z vs207+Z at(0,0,-1)");

        // Select this orientation from the actual selectable table, never invent
        // an unsupported native rotation index for a convenient test transform.
        var orientation=rotations.First(r=>GlassShapeContactModel.TryCanonicalRotation(r,out var c)&&
            c.M00==0&&c.M01==0&&c.M02==-1&&c.M10==0&&c.M11==1&&c.M12==0&&c.M20==1&&c.M21==0&&c.M22==0);
        var cornerNeighbour=AxisBrick(corner,policy,orientation,new GlassPolygonPoint(-2,0,1),zero,1,materials,2);
        ProfileMismatch(target.Groups.Single(g=>g.Bit==0),cornerNeighbour.Groups.Single(g=>g.Bit==4),0,"209-X vs rotated209-Z at(-2,0,1)");

        foreach(int bit in new[]{0,4,2})
        {
            var coverage=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(cellSize:64));coverage.Upsert(target);
            int nextId=10;
            // Initial2x2 grid covers the nominal profile while leaving native
            // rounded-corner protrusions beyond nominal x=.5/z=1.5 exposed.
            foreach(var position in CornerCubeGrid(bit,false))
                coverage.Upsert(AxisBrick(cube,policy,Identity,position,zero,1,materials,nextId++));
            Check((coverage.GetMask(1)&(1u<<bit))==0,$"actual209 cap bit={bit} exposed by undersized2x2 cube footprint");
            // Add only the missing outer row/column; no test geometry is altered.
            foreach(var position in CornerCubeGrid(bit,true))
                if(!CornerCubeGrid(bit,false).Any(p=>p.X==position.X&&p.Y==position.Y&&p.Z==position.Z))
                    coverage.Upsert(AxisBrick(cube,policy,Identity,position,zero,1,materials,nextId++));
            Check((coverage.GetMask(1)&(1u<<bit))!=0,$"actual209 cap bit={bit} fully covered by complete unit-cube union");
            Check(coverage.BudgetFailures==0,"actual209 cube union exact coverage within budget");
            // Every cube retains its opposing cap because it has exposed area
            // outside the rounded band (including its inner opening).
            for(int id=10;id<nextId;id++)Check((coverage.GetMask(id)&(1u<<(bit^1)))==0,"covering cube's exposed opposite cap retained");
            Console.WriteLine($"209CUBES bit={bit} partial4=visible complete{nextId-10}=hidden reverseCubeCaps=visible");
        }
    }
    static IEnumerable<GlassPolygonPoint> CornerCubeGrid(int bit,bool complete)
    {
        if(bit==0)
        {foreach(int y in new[]{0,1})foreach(int z in complete?new[]{0,1,2}:new[]{0,1})yield return new GlassPolygonPoint(-2,y,z);}
        else if(bit==4)
        {foreach(int x in complete?new[]{-1,0,1}:new[]{-1,0})foreach(int y in new[]{0,1})yield return new GlassPolygonPoint(x,y,-1);}
        else
        {foreach(int x in complete?new[]{-1,0,1}:new[]{-1,0})foreach(int z in complete?new[]{0,1,2}:new[]{0,1})yield return new GlassPolygonPoint(x,-1,z);}
    }
    static void ProfileMismatch(GlassPolygonFaceGroup a,GlassPolygonFaceGroup b,int axis,string name)
    {
        Check(a.Triangles.Length==10&&b.Triangles.Length==10,name+" actual10trianglecaps");
        double plane=a.Triangles[0].A.Axis(axis);
        foreach(var triangle in a.Triangles.Concat(b.Triangles))
            Check(triangle.A.Axis(axis)==plane&&triangle.B.Axis(axis)==plane&&triangle.C.Axis(axis)==plane,name+" exactly common cap plane");
        var coverage=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(cellSize:64));
        coverage.Upsert(new GlassPolygonCoverageBrick(1,1,255,a));coverage.Upsert(new GlassPolygonCoverageBrick(1,2,255,b));
        Check(coverage.GetMask(1)==0&&coverage.GetMask(2)==0,name+" actual profile mismatch retains both caps");
        Check(coverage.BudgetFailures==0,name+" noncoverage is geometric, not budget fallback");
        // Control: replacing only the neighbour's test triangle geometry with
        // the exact opposite target must make both masks hidden. This confirms
        // the retained result above is not an inactive plane/winding fixture.
        var exact=new GlassPolygonFaceGroup(b.Bit,a.Triangles.Select(t=>new GlassPolygonTriangle(t.A,t.C,t.B)).ToArray());
        coverage.Upsert(new GlassPolygonCoverageBrick(1,2,255,exact));
        Check(coverage.GetMask(1)==(1u<<a.Bit)&&coverage.GetMask(2)==(1u<<b.Bit),name+" identical-opposite control fully covered");
        double discrepancy=Math.Max(NearestProjected(a,b,axis),NearestProjected(b,a,axis));
        Check(discrepancy>.0005&&discrepancy<.003,name+" measured real profile discrepancy survives contact policy");
        Console.WriteLine($"209PROFILE {name}: bothVisible=true identicalOppositeControl=hidden nearestVertexInfinity={discrepancy:R}");
    }
    static double NearestProjected(GlassPolygonFaceGroup a,GlassPolygonFaceGroup b,int axis)
    {
        var pointsA=a.Triangles.SelectMany(t=>new[]{t.A,t.B,t.C});var pointsB=b.Triangles.SelectMany(t=>new[]{t.A,t.B,t.C}).ToArray();
        int u=(axis+1)%3,v=(axis+2)%3;
        return pointsA.Max(p=>pointsB.Min(q=>Math.Max(Math.Abs(p.Axis(u)-q.Axis(u)),Math.Abs(p.Axis(v)-q.Axis(v)))));
    }
}
