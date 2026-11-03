using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace GraphicalTweaks
{
    internal readonly struct GlassMixedCapMaterial
    {
        internal readonly int Id,DiffuseIndex,SpecularIndex,NormalIndex,NormalMode,PixelMode;
        internal readonly GlassShapeDeformationMaterial Deformation;
        internal GlassMixedCapMaterial(int id,GlassShapeDeformationMaterial deformation,int diffuseIndex=183,
            int specularIndex=154,int normalIndex=-1,int normalMode=0,int pixelMode=0)
        {Id=id;Deformation=deformation;DiffuseIndex=diffuseIndex;SpecularIndex=specularIndex;NormalIndex=normalIndex;NormalMode=normalMode;PixelMode=pixelMode;}
    }
    internal sealed class GlassMixedCapSource
    {
        internal readonly int BrickId,ActualTriangles,PaddedTriangles;
        internal string SourceHash=>definition.Hash;
        private readonly MixedDefinition definition;
        private GlassMixedCapSource(MixedDefinition definition)
        {this.definition=definition;BrickId=definition.Id;ActualTriangles=definition.Actual;PaddedTriangles=definition.Padded;}
        internal static bool TryAdmit(int id,string hash,int actual,int padded,int[][] groups,
            IReadOnlyDictionary<int,GlassMixedCapMaterial> materials,out GlassMixedCapSource source)
        {
            source=null;
            var d=GlassMixedCapCatalog.Definition(id);
            if(d==null||!GlassMixedCapCatalog.AdmissionMatches(d,hash,actual,padded,groups,materials))return false;
            source=new GlassMixedCapSource(d);return true;
        }
        internal bool RawMaterialsMatch(byte[] records)
        {
            foreach(var group in definition.Groups)foreach(var triangle in group.Triangles)for(int v=0;v<3;v++)
                if(BitConverter.ToUInt32(records,(triangle.NativeIndex*3+v)*124+60)!=(uint)group.Material)return false;
            return true;
        }
        internal bool TryBuildGeometry(long entity,int buildId,byte health,int countX,int countY,int countZ,float unit,
            GlassShapeRotation rotation,GlassPolygonPoint placement,out GlassPolygonCoverageBrick brick,out GlassMixedCapGroupMap[] correspondence)
        {
            brick=default;correspondence=null;
            if(buildId<1||buildId>4194303||health!=255||countX!=0||countY!=0||countZ!=0||!AuditedUnit(unit)||
               !Grid(placement.X)||!Grid(placement.Y)||!Grid(placement.Z)||
               !GlassShapeContactModel.TryCanonicalRotation(rotation,out var r))return false;
            var groups=new List<GlassPolygonFaceGroup>();var maps=new List<GlassMixedCapGroupMap>();
            foreach(var group in definition.Groups)
            {
                var triangles=new List<GlassPolygonTriangle>();var native=new List<int>();var whole=new List<int>();bool collapsed=false;
                foreach(var src in group.Triangles)
                {
                    whole.Add(src.NativeIndex);
                    var t=new GlassPolygonTriangle(Transform(src.Triangle.A,r,unit,placement),Transform(src.Triangle.B,r,unit,placement),Transform(src.Triangle.C,r,unit,placement));
                    if(!Coordinate(t.A)||!Coordinate(t.B)||!Coordinate(t.C))return false;
                    if(Degenerate(t)){collapsed=true;continue;}
                    triangles.Add(t);native.Add(src.NativeIndex);
                }
                if(triangles.Count==0)return false;
                groups.Add(new GlassPolygonFaceGroup(group.Bit,triangles.ToArray()));
                maps.Add(new GlassMixedCapGroupMap(group.Bit,collapsed,native.ToArray(),whole.ToArray()));
            }
            brick=new GlassPolygonCoverageBrick(entity,buildId,health,groups.ToArray());correspondence=maps.ToArray();return true;
        }
        private static GlassPolygonPoint Transform(GlassPolygonPoint p,GlassShapeRotation r,float u,GlassPolygonPoint o)
            =>new GlassPolygonPoint((r.M00*p.X+r.M01*p.Y+r.M02*p.Z)*u+o.X,(r.M10*p.X+r.M11*p.Y+r.M12*p.Z)*u+o.Y,(r.M20*p.X+r.M21*p.Y+r.M22*p.Z)*u+o.Z);
        private static bool Degenerate(GlassPolygonTriangle t)
        {
            double x=t.B.X-t.A.X,y=t.B.Y-t.A.Y,z=t.B.Z-t.A.Z,u=t.C.X-t.A.X,v=t.C.Y-t.A.Y,w=t.C.Z-t.A.Z;
            return y*w-z*v==0&&z*u-x*w==0&&x*v-y*u==0;
        }
        private static bool AuditedUnit(float u){for(int i=0;i<8;i++)if(u==.125f*(1<<i))return true;return false;}
        private static bool Coordinate(GlassPolygonPoint p)=>Finite(p.X)&&Finite(p.Y)&&Finite(p.Z);
        private static bool Finite(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v)&&Math.Abs(v)<=1048576;
        private static bool Grid(double v)=>Finite(v)&&v*32==Math.Truncate(v*32);
    }
    internal static class GlassMixedCapCatalog
    {
        internal static bool TryBuild(GlassMixedCapSource source,long entity,int buildId,byte health,int countX,int countY,int countZ,
            float unit,GlassShapeRotation rotation,GlassPolygonPoint exactPlacement,out GlassMixedCapInstance instance)
        =>GlassMixedCapInstance.TryCreate(source,entity,buildId,health,countX,countY,countZ,unit,rotation,exactPlacement,out instance);
        // Cached-hash entry point for a trusted native source binder. The hash,
        // grouping and used-cap style objects MUST come from the same immutable
        // native snapshot. Prefer TryCreate when the record bytes are available.
        internal static bool TryAdmit(int brickId,string authoritativeSourceSha256,int actualTriangles,int paddedTriangles,
            int[][] allGroups,IReadOnlyDictionary<int,GlassMixedCapMaterial> actualCapMaterials,out GlassMixedCapSource source)
        =>GlassMixedCapSource.TryAdmit(brickId,authoritativeSourceSha256,actualTriangles,paddedTriangles,allGroups,actualCapMaterials,out source);
        internal static bool TryCreate(int brickId,byte[] fullPaddedRecords,int actualTriangles,int paddedTriangles,
            int[][] allGroups,IReadOnlyDictionary<int,GlassMixedCapMaterial> actualCapMaterials,out GlassMixedCapSource source)
        {
            source=null;
            if(fullPaddedRecords==null||paddedTriangles<1||(long)paddedTriangles*3*124!=fullPaddedRecords.Length)return false;
            // Hash and material correspondence must inspect the same owned
            // snapshot even if the caller later reuses its capture buffer.
            var records=(byte[])fullPaddedRecords.Clone();string hash;
            using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(records)).Replace("-","");
            if(!TryAdmit(brickId,hash,actualTriangles,paddedTriangles,allGroups,actualCapMaterials,out var admitted)||!admitted.RawMaterialsMatch(records))return false;
            source=admitted;return true;
        }
        internal static bool AdmissionMatches(MixedDefinition d,string hash,int actual,int padded,int[][] groups,
            IReadOnlyDictionary<int,GlassMixedCapMaterial> materials)
        {
            if(!string.Equals(hash,d.Hash,StringComparison.OrdinalIgnoreCase)||actual!=d.Actual||padded!=d.Padded||
               groups==null||groups.Length!=d.AllGroups.Length||materials==null)return false;
            for(int g=0;g<groups.Length;g++)
            {
                if(groups[g]==null||groups[g].Length!=d.AllGroups[g].Length)return false;
                for(int t=0;t<groups[g].Length;t++)if(groups[g][t]!=d.AllGroups[g][t])return false;
            }
            foreach(var group in d.Groups)
                if(!materials.TryGetValue(group.Material,out var m)||m.Id!=group.Material||!StyleMatches(m))return false;
            return true;
        }
        private static bool StyleMatches(GlassMixedCapMaterial m)
        {
            var d=m.Deformation;
            return d.ResizeMode==0&&d.VertexMode==0&&d.AlphaMode==2&&d.ResizeUnit==1&&d.GeometryLow==-.001f&&d.GeometryHigh==.001f&&
                d.BorderIndex==-1&&d.BuildingOffset==0&&m.DiffuseIndex==183&&m.SpecularIndex==154&&m.NormalIndex==-1&&m.NormalMode==0&&m.PixelMode==0;
        }
        // Frozen semantic half-grid controls. These are ideal triangle/band
        // interfaces, not replacement physical vertices. Native order/corner
        // correspondence is fixed by the full source SHA and group lists.
        internal static MixedDefinition Definition(int id)
        {
            if(id==118)return new MixedDefinition(118,8,12,"06AD79D6C5B28D3151AA37B81CBE8965D5B0B04F7FEB92D67DFA331E941FF6AB",
                new[]{new[]{2,3},new[]{4,5},new[]{6,7}},new[]{
                    G(0,271,T(2,-3,4,-1,-3,2,-1,-3,0,1),T(3,-3,4,-1,-3,0,1,-3,0,3)),
                    G(1,271,T(4,-1,0,-1,1,0,-1,-3,0,3),T(5,-1,0,-1,-3,0,3,-3,0,1)),
                    G(2,271,T(6,-1,0,-1,-3,2,-1,-3,4,-1),T(7,-1,0,-1,-3,4,-1,1,0,-1))});
            if(id==313)return new MixedDefinition(313,18,24,"521E1FFA34AB67B0022080BC55A273B450BA0F99D9205AC7D51CDC5B8493E024",
                new[]{new[]{0},new[]{8,9},new[]{10,11},new[]{12,13},new[]{14,15},new[]{16,17}},new[]{
                    G(0,696,T(0,1,4,-1,1,2,-1,-1,4,-1)),
                    G(1,696,T(8,-3,4,3,-3,4,1,-3,2,3),T(9,-3,4,3,-3,2,3,-3,4,3)),
                    G(2,696,T(10,1,0,1,1,0,3,1,0,3),T(11,1,0,1,1,0,3,-1,0,3)),
                    G(3,695,T(12,-3,4,3,1,4,-1,-1,4,-1),T(13,-3,4,3,-1,4,-1,-3,4,1)),
                    G(4,695,T(14,1,0,3,-3,4,3,-3,2,3),T(15,1,0,3,-3,2,3,-1,0,3)),
                    G(5,695,T(16,1,0,1,1,2,-1,1,4,-1),T(17,1,0,1,1,4,-1,1,0,3))});
            if(id==314)return new MixedDefinition(314,4,12,"10B78DE3CCB39A1EA78FC9CB7F89DE47C4F1EAE1C5BA9F462CDDF0151351176D",
                new[]{new[]{0},new[]{1},new[]{2},new[]{3}},new[]{
                    G(1,698,T(1,-1,0,-1,1,0,-1,-1,0,1)),
                    G(2,698,T(2,-1,0,-1,-1,2,-1,1,0,-1)),
                    G(3,698,T(3,-1,0,1,-1,2,-1,-1,0,-1))});
            return null;
        }
        private static MixedTriangle T(int native,params int[] h)=>new MixedTriangle(native,
            new GlassPolygonPoint(h[0]/2d,h[1]/2d,h[2]/2d),new GlassPolygonPoint(h[3]/2d,h[4]/2d,h[5]/2d),new GlassPolygonPoint(h[6]/2d,h[7]/2d,h[8]/2d));
        private static MixedGroup G(int bit,int material,params MixedTriangle[] triangles)=>new MixedGroup(bit,material,triangles);
    }
    internal sealed class GlassMixedCapGroupMap
    {
        // Whole-group masks use the explicit nominal-interface approximation:
        // all original group triangles, including collapsed bevel inputs, may
        // be hidden. Partial clipping is separately refused for such groups.
        internal readonly int Bit;
        internal readonly bool HasCollapsedInputs;
        internal readonly IReadOnlyList<int> NativeTriangleIndices,WholeGroupNativeTriangleIndices;
        internal GlassMixedCapGroupMap(int bit,bool collapsed,int[] native,int[] whole)
        {Bit=bit;HasCollapsedInputs=collapsed;NativeTriangleIndices=Array.AsReadOnly((int[])native.Clone());WholeGroupNativeTriangleIndices=Array.AsReadOnly((int[])whole.Clone());}
    }
    internal sealed class GlassMixedCapInstance
    {
        private readonly GlassPolygonCoverageBrick brick;
        private readonly double[] low=new double[3],high=new double[3];
        internal readonly int BrickId;
        internal readonly IReadOnlyList<GlassMixedCapGroupMap> Groups;
        internal long Entity=>brick.Entity;
        internal int BuildId=>brick.BuildId;
        private GlassMixedCapInstance(int brickId,GlassPolygonCoverageBrick brick,GlassMixedCapGroupMap[] groups)
        {
            BrickId=brickId;this.brick=Clone(brick);Groups=Array.AsReadOnly((GlassMixedCapGroupMap[])groups.Clone());
            for(int a=0;a<3;a++)
            {
                low[a]=double.PositiveInfinity;high[a]=double.NegativeInfinity;
                foreach(var g in this.brick.Groups)foreach(var t in g.Triangles)foreach(var v in new[]{t.A,t.B,t.C})
                {low[a]=Math.Min(low[a],v.Axis(a));high[a]=Math.Max(high[a],v.Axis(a));}
            }
        }
        internal GlassPolygonCoverageBrick Brick=>Clone(brick);
        internal static bool TryCreate(GlassMixedCapSource source,long entity,int buildId,byte health,int countX,int countY,int countZ,
            float unit,GlassShapeRotation rotation,GlassPolygonPoint placement,out GlassMixedCapInstance instance)
        {
            instance=null;
            if(source==null||!source.TryBuildGeometry(entity,buildId,health,countX,countY,countZ,unit,rotation,placement,out var brick,out var groups))return false;
            instance=new GlassMixedCapInstance(source.BrickId,brick,groups);return true;
        }
        private static GlassPolygonCoverageBrick Clone(GlassPolygonCoverageBrick brick)=>new GlassPolygonCoverageBrick(brick.Entity,brick.BuildId,brick.Health,
            Array.ConvertAll(brick.Groups,g=>new GlassPolygonFaceGroup(g.Bit,(GlassPolygonTriangle[])g.Triangles.Clone())));
        internal bool Overlaps(GlassMixedCapInstance other)
        {
            if(Entity!=other.Entity)return false;
            for(int a=0;a<3;a++)if(low[a]>other.high[a]||other.low[a]>high[a])return false;
            return true;
        }
        internal bool SameGeometry(GlassMixedCapInstance other)
        {
            if(ReferenceEquals(this,other))return true;var x=brick;var y=other.brick;
            if(BrickId!=other.BrickId||x.Entity!=y.Entity||x.BuildId!=y.BuildId||x.Groups.Length!=y.Groups.Length)return false;
            for(int g=0;g<x.Groups.Length;g++)
            {
                var p=x.Groups[g];var q=y.Groups[g];if(p.Bit!=q.Bit||p.Triangles.Length!=q.Triangles.Length)return false;
                for(int t=0;t<p.Triangles.Length;t++)if(!Equal(p.Triangles[t].A,q.Triangles[t].A)||!Equal(p.Triangles[t].B,q.Triangles[t].B)||!Equal(p.Triangles[t].C,q.Triangles[t].C))return false;
            }
            return true;
        }
        private static bool Equal(GlassPolygonPoint p,GlassPolygonPoint q)=>p.X==q.X&&p.Y==q.Y&&p.Z==q.Z;
    }
    // Own coverage universe: nominal controls never enter an actual-geometry
    // occlusion index. This prototype has no native/GPU publication path.
    internal sealed class GlassMixedCapScene
    {
        private readonly Dictionary<int,GlassMixedCapInstance> owners=new Dictionary<int,GlassMixedCapInstance>();
        private readonly GlassPolygonCoverageModel model=new GlassPolygonCoverageModel();
        private readonly HashSet<int> touched=new HashSet<int>();
        private readonly List<GlassPolygonCoverageChange> discardedMaskChanges=new List<GlassPolygonCoverageChange>();
        private readonly int maximumOwners;
        internal GlassMixedCapScene(int maximumOwners=4096)
        {if(maximumOwners<1||maximumOwners>4096)throw new ArgumentOutOfRangeException(nameof(maximumOwners));this.maximumOwners=maximumOwners;}
        internal int OwnerCount=>owners.Count;
        internal long CandidateChecks=>model.CandidateChecks;
        internal long BudgetFailures=>model.BudgetFailures;
        internal uint GetMask(int buildId)=>model.GetMask(buildId);
        internal bool TryGetInstance(int buildId,out GlassMixedCapInstance instance)=>owners.TryGetValue(buildId,out instance);
        internal bool TryGetCapRemainder(int buildId,int groupBit,out GlassCapClipResult result)
        {
            result=null;if(!owners.TryGetValue(buildId,out var owner))return false;
            foreach(var g in owner.Groups)if(g.Bit==groupBit)
                return !g.HasCollapsedInputs&&model.TryGetCapRemainder(buildId,groupBit,out result);
            return false;
        }
        internal bool TryUpsert(GlassMixedCapSource source,long entity,int buildId,byte health,int countX,int countY,int countZ,
            float unit,GlassShapeRotation rotation,GlassPolygonPoint placement)
        {
            if(!GlassMixedCapCatalog.TryBuild(source,entity,buildId,health,countX,countY,countZ,unit,rotation,placement,out var instance))
            {Remove(buildId);return false;}
            return Upsert(instance);
        }
        internal bool Upsert(GlassMixedCapInstance instance)
        {
            if(instance==null)return false;
            if(owners.TryGetValue(instance.BuildId,out var old)&&old.SameGeometry(instance))return true;
            Remove(instance.BuildId);
            touched.Add(instance.BuildId);
            if(owners.Count>=maximumOwners)return false;
            TouchNeighbours(instance);var brick=instance.Brick;
            long failures=model.BudgetFailures;model.Upsert(brick);DiscardMaskChanges();
            if(model.BudgetFailures!=failures){model.Remove(brick.BuildId);DiscardMaskChanges();return false;}
            owners.Add(brick.BuildId,instance);return true;
        }
        internal void Remove(int buildId)
        {
            if(!owners.TryGetValue(buildId,out var old))return;
            TouchNeighbours(old);touched.Add(buildId);owners.Remove(buildId);model.Remove(buildId);DiscardMaskChanges();
        }
        internal void RemoveEntity(long entity)
        {
            var ids=new List<int>();foreach(var p in owners)if(p.Value.Entity==entity)ids.Add(p.Key);
            foreach(int id in ids)Remove(id);
        }
        internal void Clear()
        {foreach(int id in owners.Keys)touched.Add(id);owners.Clear();model.Clear();DiscardMaskChanges();}
        internal void DrainTouched(List<int> output)
        {
            if(output==null)throw new ArgumentNullException(nameof(output));
            var ids=new List<int>(touched);ids.Sort();output.AddRange(ids);touched.Clear();
        }
        private void TouchNeighbours(GlassMixedCapInstance instance)
        {foreach(var p in owners)if(instance.Overlaps(p.Value))touched.Add(p.Key);}
        // This wrapper publishes geometry touches and reads current full masks;
        // it never publishes the inner model's mask-only delta stream. Drain it
        // per mutation so retired build IDs cannot accumulate across lifetimes.
        private void DiscardMaskChanges()
        {model.DrainChanges(discardedMaskChanges);discardedMaskChanges.Clear();}
    }
    internal sealed class MixedDefinition
    {
        internal readonly int Id,Actual,Padded;internal readonly string Hash;
        internal readonly int[][] AllGroups;internal readonly MixedGroup[] Groups;
        internal MixedDefinition(int id,int actual,int padded,string hash,int[][] allGroups,MixedGroup[] groups)
        {Id=id;Actual=actual;Padded=padded;Hash=hash;AllGroups=allGroups;Groups=groups;}
    }
    internal sealed class MixedGroup
    {
        internal readonly int Bit,Material;internal readonly MixedTriangle[] Triangles;
        internal MixedGroup(int bit,int material,MixedTriangle[] triangles){Bit=bit;Material=material;Triangles=triangles;}
    }
    internal readonly struct MixedTriangle
    {
        internal readonly int NativeIndex;internal readonly GlassPolygonTriangle Triangle;
        internal MixedTriangle(int native,GlassPolygonPoint a,GlassPolygonPoint b,GlassPolygonPoint c)
        {NativeIndex=native;Triangle=new GlassPolygonTriangle(a,b,c);}
    }
}
