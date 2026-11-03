using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GraphicalTweaks;

static partial class Program
{
    sealed class Shape
    {
        internal int Id,Count;internal byte[] Bytes;
        internal GlassShapeFloat3 Position(int i)=>Read3(Bytes,i*124);
        internal GlassShapeDeformationInput InputAt(int i,GlassShapeRotation rotation,GlassPolygonPoint position,int[] counts,float unit,
            Dictionary<int,GlassShapeDeformationMaterial> materials)
        {
            int at=i*124;
            return new GlassShapeDeformationInput(Read3(Bytes,at),Read3(Bytes,at+12),Read3(Bytes,at+64),Read3(Bytes,at+76),materials[BitConverter.ToInt32(Bytes,at+60)],
                counts[0],counts[1],counts[2],unit,rotation,V((float)position.X,(float)position.Y,(float)position.Z));
        }
    }
    static GlassShapeFloat3 Read3(byte[] b,int at)=>V(BitConverter.ToSingle(b,at),BitConverter.ToSingle(b,at+4),BitConverter.ToSingle(b,at+8));
    static void Live(string path,bool joinsOnly=false)
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        var ids=new HashSet<int>(new[]{166,168,314,315,316,167,118,313,317,207,209});
        var mats=new Dictionary<int,GlassShapeDeformationMaterial>();
        foreach(var line in File.ReadLines(Path.Combine(path,"materials.csv")).Skip(1))
        {
            var c=line.Split(',');if(!ids.Contains(int.Parse(c[0]))||c[3]!="transparent"||c[4]!="none"||c[5]!="-1"||c[12]!="off")continue;
            int mode=c[9]=="free"?0:c[9]=="radiusXY"?7:c[9]=="radiusXYZ"?8:-1;
            mats.Add(int.Parse(c[2]),new GlassShapeDeformationMaterial(mode,0,2,float.Parse(c[8]),float.Parse(c[10]),float.Parse(c[11])));
        }
        var shapes=new List<Shape>();
        foreach(var line in File.ReadLines(Path.Combine(path,"catalog.csv")).Skip(1))
        {
            var c=line.Split(',');int id=int.Parse(c[0]);if(!ids.Contains(id))continue;
            shapes.Add(new Shape{Id=id,Count=int.Parse(c[3]),Bytes=File.ReadAllBytes(Path.Combine(path,$"{id}-cpu.bin"))});
        }
        Check(shapes.Count==11,"exact eleven clear shapes captured");
        var positions=shapes.SelectMany(s=>Enumerable.Range(0,s.Count).Select(s.Position)).ToArray();
        Check(GlassShapeContactCanonicalizer.TryCreate(positions,out var policy),"actual bounded source policy");
        Check(policy.SourceMagnitudeCount==150&&policy.CanonicalMagnitudeCount==136,"actual magnitude cluster count 150 to 136");
        Check(policy.MaximumDisplacement==8.344650268554688e-7,"actual source component displacement bound");
        foreach(var shape in shapes)for(int i=0;i<shape.Count;i+=3)
        {
            var p=new GlassShapeFloat3[3];for(int j=0;j<3;j++)Check(policy.TryCanonicalize(shape.Position(i+j),out p[j]),"all triangle source coordinates known");
            var raw=Cross(shape.Position(i),shape.Position(i+1),shape.Position(i+2));var adjusted=Cross(p[0],p[1],p[2]);
            Check(raw.X*adjusted.X+raw.Y*adjusted.Y+raw.Z*adjusted.Z>0,"source canonicalization preserves triangle orientation and nondegeneracy");
        }
        float[][] rows=Enumerable.Range(0,64).Select(_=>new float[16]).ToArray();
        foreach(var line in File.ReadLines(Path.Combine(path,"rotations.csv")).Skip(1)){var c=line.Split(',');rows[int.Parse(c[0])][int.Parse(c[1])*4+int.Parse(c[2])]=float.Parse(c[3]);}
        var rotations=rows.Take(32).Select(a=>new GlassShapeRotation(a)).ToArray();
        foreach(var rotation in rotations)Check(GlassShapeContactModel.TryCanonicalRotation(rotation,out _),"actual selectable rotation admitted");
        if(joinsOnly){Joins(shapes,policy,rotations,mats);return;}
        int samples=0,fallbacks=0;double maxDistance=0,maxOriginDistance=0,maxLocalDistance=0;var distances=new Dictionary<int,double>();
        foreach(var shape in shapes)
        {
            double maximum=0;
            foreach(int[] counts in new[]{new[]{0,0,0},new[]{1,1,1},new[]{15,15,15},new[]{1,2,3},new[]{15,0,7}})
            foreach(float unit in new[]{1f,.25f})foreach(var rotation in rotations)
            foreach(var position in new[]{default(GlassPolygonPoint),new GlassPolygonPoint(17.03125,-2.0625,.125),new GlassPolygonPoint(8192.03125,-8192.0625,4096.125)})
            for(int i=0;i<shape.Count;i++)
            {
                var input=shape.InputAt(i,rotation,position,counts,unit,mats);
                bool accepted=GlassShapeContactModel.TryDeform(input,policy,position,out var contact);samples++;
                if(!accepted){fallbacks++;continue;}
                Check(GlassShapeDeformationModel.TryDeform(input,out var raw),"raw shader adapter admits contact case");
                double dx=contact.X-raw.X,dy=contact.Y-raw.Y,dz=contact.Z-raw.Z;
                double distance=Math.Sqrt(dx*dx+dy*dy+dz*dz);maximum=Math.Max(maximum,distance);maxDistance=Math.Max(maxDistance,distance);
            if(position.X==0&&position.Y==0&&position.Z==0)
            {
                maxOriginDistance=Math.Max(maxOriginDistance,distance);
                if(rotation.Equals(Identity))maxLocalDistance=Math.Max(maxLocalDistance,distance);
            }
            }
            distances.Add(shape.Id,maximum);
        }
        Console.WriteLine($"SWEEP shapes={shapes.Count} sourceRecords={positions.Length} cases={samples} fallbacks={fallbacks} maxWorldRawContactDistance={maxDistance:R}");
        Console.WriteLine($"DISPLACEMENT sourceComponent={policy.MaximumDisplacement:R} localEuclidean={maxLocalDistance:R} rotatedAtOriginEuclidean={maxOriginDistance:R}");
        foreach(var distance in distances)Console.WriteLine($"DISTANCE id={distance.Key} max={distance.Value:R}");
        Check(fallbacks==0,"all actual source/count/unit/rotation/grid cases admit contact geometry");
        Joins(shapes,policy,rotations,mats);
    }
    static GlassPolygonPoint Cross(GlassShapeFloat3 a,GlassShapeFloat3 b,GlassShapeFloat3 c)
    {
        double x=(double)b.X-a.X,y=(double)b.Y-a.Y,z=(double)b.Z-a.Z,u=(double)c.X-a.X,v=(double)c.Y-a.Y,w=(double)c.Z-a.Z;
        return new GlassPolygonPoint(y*w-z*v,z*u-x*w,x*v-y*u);
    }
    static GlassPolygonCoverageBrick AxisBrick(Shape shape,GlassShapeContactCanonicalizer policy,GlassShapeRotation rotation,GlassPolygonPoint position,int[] counts,float unit,
        Dictionary<int,GlassShapeDeformationMaterial> materials,int buildId)
    {
        var groups=new Dictionary<int,List<GlassPolygonTriangle>>();
        for(int i=0;i<shape.Count;i+=3)
        {
            var source=new GlassShapeFloat3[3];for(int j=0;j<3;j++)Check(policy.TryCanonicalize(shape.Position(i+j),out source[j]),"triangle source known");
            int axis=-1;for(int a=0;a<3;a++)if(Axis(source[0],a)==Axis(source[1],a)&&Axis(source[1],a)==Axis(source[2],a)){axis=a;break;}
            if(axis<0)continue;
            var points=new GlassPolygonPoint[3];for(int j=0;j<3;j++)Check(GlassShapeContactModel.TryDeform(shape.InputAt(i+j,rotation,position,counts,unit,materials),policy,position,out points[j]),"cap deformation admitted");
            double[] a1={source[1].X-source[0].X,source[1].Y-source[0].Y,source[1].Z-source[0].Z};
            double[] a2={source[2].X-source[0].X,source[2].Y-source[0].Y,source[2].Z-source[0].Z};
            double normal=a1[(axis+1)%3]*a2[(axis+2)%3]-a1[(axis+2)%3]*a2[(axis+1)%3];
            Check(normal!=0,"canonical source triangle not degenerate");
            int bit=axis*2+(normal>0?1:0);if(!groups.TryGetValue(bit,out var list))groups.Add(bit,list=new List<GlassPolygonTriangle>());
            list.Add(new GlassPolygonTriangle(points[0],points[1],points[2]));
        }
        return new GlassPolygonCoverageBrick(1,buildId,255,groups.Select(g=>new GlassPolygonFaceGroup(g.Key,g.Value.ToArray())).ToArray());
    }
    static float Axis(GlassShapeFloat3 p,int a)=>a==0?p.X:a==1?p.Y:p.Z;
    static void Joins(List<Shape> shapes,GlassShapeContactCanonicalizer policy,GlassShapeRotation[] rotations,Dictionary<int,GlassShapeDeformationMaterial> mats)
    {
        foreach(int id in new[]{166,167,207})foreach(int n in new[]{0,1,15})foreach(float unit in new[]{1f,.25f})foreach(var rotation in rotations)
        {
            var shape=shapes.Single(s=>s.Id==id);var counts=new[]{n,n,n};
            Check(GlassShapeContactModel.TryCanonicalRotation(rotation,out var r),"join canonical rotation");
            double length=(1+n)*unit;
            var position=new GlassPolygonPoint(r.M02*length,r.M12*length,r.M22*length);
            var coverage=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(cellSize:64));
            coverage.Upsert(AxisBrick(shape,policy,rotation,default,counts,unit,mats,1));
            coverage.Upsert(AxisBrick(shape,policy,rotation,position,counts,unit,mats,2));
            Check((coverage.GetMask(1)&32)!=0&&(coverage.GetMask(2)&16)!=0,$"repeated extrusion hidden id={id} count={n} unit={unit}");
            Check(coverage.BudgetFailures==0,"join exact coverage within budget");
            // Genuine grid separation is never repaired by source/contact rules.
            position=new GlassPolygonPoint(position.X+r.M02/32d,position.Y+r.M12/32d,position.Z+r.M22/32d);
            coverage.Upsert(AxisBrick(shape,policy,rotation,position,counts,unit,mats,2));
            Check((coverage.GetMask(1)&32)==0&&(coverage.GetMask(2)&16)==0,"separated grid instances stay uncovered");
        }
        foreach(int id in new[]{168,314,315,316})
        {
            var cube=shapes.Single(s=>s.Id==166);var smaller=shapes.Single(s=>s.Id==id);
            var coverage=new GlassPolygonCoverageModel();int[] counts={0,0,0};
            coverage.Upsert(AxisBrick(smaller,policy,rotations[0],default,counts,1,mats,1));
            coverage.Upsert(AxisBrick(cube,policy,rotations[0],new GlassPolygonPoint(-1,0,0),counts,1,mats,2));
            Check((coverage.GetMask(1)&1)!=0,"mixed cube covers core -X cap "+id);
        }
        // Every admitted clear shape has actual caps which can be covered by a
        // large resized cube. This does not assert universal pairwise matching.
        var largeCube=shapes.Single(s=>s.Id==166);
        var cubeLocal=AxisBrick(largeCube,policy,Identity,default,new[]{15,15,15},1,mats,2);
        foreach(var shape in shapes)
        {
            var target=AxisBrick(shape,policy,Identity,default,new[]{0,0,0},1,mats,1);
            foreach(var group in target.Groups)
            {
                int axis=group.Bit/2;double plane=group.Triangles[0].A.Axis(axis);
                // Only coherent source planes are selected by this fixture.
                Check(group.Triangles.All(t=>t.A.Axis(axis)==plane&&t.B.Axis(axis)==plane&&t.C.Axis(axis)==plane),"axis group coherent before cube coverage");
                double cubePlane=cubeLocal.Groups.Single(g=>g.Bit==(group.Bit^1)).Triangles[0].A.Axis(axis);
                double[] offset={-4,-4,-4};offset[axis]=plane-cubePlane;
                var covering=AxisBrick(largeCube,policy,Identity,new GlassPolygonPoint(offset[0],offset[1],offset[2]),new[]{15,15,15},1,mats,2);
                var coverage=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(cellSize:64));coverage.Upsert(target);coverage.Upsert(covering);
                Check((coverage.GetMask(1)&(1u<<group.Bit))!=0,$"all11 actual cap covered id={shape.Id} bit={group.Bit} plane={plane:R} offset={string.Join('/',offset)} mask={coverage.GetMask(1)} budget={coverage.BudgetFailures} triangles={group.Triangles.Length}");
            }
        }
        // Resizing preserves these three cap planes; actual profile-mismatch
        // coverage and mixed cube unions are separately exercised below.
        var corner=shapes.Single(s=>s.Id==209);
        foreach(int n in new[]{0,1,15})
        {
            var brick=AxisBrick(corner,policy,rotations[0],default,new[]{n,n,n},1,mats,1);
            Check(brick.Groups.Length==3,"209 retains exactly three nominal cap directions");
            foreach(var group in brick.Groups)
            {
                int axis=group.Bit/2;double plane=group.Triangles[0].A.Axis(axis);
                Check(group.Triangles.Length==10,"209 each cap retains ten triangles");
                foreach(var triangle in group.Triangles)Check(triangle.A.Axis(axis)==plane&&triangle.B.Axis(axis)==plane&&triangle.C.Axis(axis)==plane,"209 deformed cap exactly planar");
            }
        }
        ActualRoundedCornerCoverage(shapes,policy,rotations,mats);
        Console.WriteLine("JOINS all32rotations: 166/167/207 counts0/1/15 units1/.25 repeated extrusion and separated-grid cases; four mixed core shapes; all11 actual cap/cube coverage; 209 cap planarity counts0/1/15.");
    }
}
