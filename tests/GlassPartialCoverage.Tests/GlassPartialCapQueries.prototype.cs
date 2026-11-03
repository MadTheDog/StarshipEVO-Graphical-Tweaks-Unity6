using System.Collections.Generic;
using System.Numerics;

namespace GraphicalTweaks
{
    internal sealed partial class GlassPolygonCoverageModel
    {
        // Query-only counters are cumulative for this model object's lifetime;
        // neither queries nor failures modify coverage masks or mask deltas.
        internal long CapQueryBudgetFailures {get;private set;}
        internal bool TryGetCapRemainder(int buildId,int groupBit,out GlassCapClipResult result)
        {
            result=null;
            if(!bricks.TryGetValue(buildId,out var state)||!state.Groups.TryGetValue(groupBit,out var faces)||faces.Count==0)return false;
            try
            {
                var output=new List<GlassTriangleRemainder>();var budget=new Budget(limits.MaximumOperations);int emitted=0;
                for(int ordinal=0;ordinal<faces.Count;ordinal++)
                {
                    Surface face=faces[ordinal];
                    var fragments=CapFragments(face,budget);
                    Number original=Cross(face.Points[0],face.Points[1],face.Points[2],budget);
                    Number remaining=Number.Zero;
                    var retained=new List<GlassBarycentricTriangle>();
                    foreach(var polygon in fragments)for(int i=1;i<polygon.Count-1;i++)
                    {
                        Number area=Cross(polygon[0],polygon[i],polygon[i+1],budget);
                        if(area.Sign==0)continue;
                        if(area.Sign!=face.Winding)throw new WorkLimit();
                        remaining+=area;
                        if(retained.Count>=limits.MaximumFragments)throw new WorkLimit();
                        retained.Add(new GlassBarycentricTriangle(CapWeights(face.Points,polygon[0],original,budget),
                            CapWeights(face.Points,polygon[i],original,budget),CapWeights(face.Points,polygon[i+1],original,budget)));
                    }
                    Number removed=original-remaining;
                    if(removed.Sign!=0&&removed.Sign!=face.Winding)throw new WorkLimit();
                    bool changed=removed.Sign!=0;
                    // Tangential/no-area intersections must not needlessly
                    // retessellate the native source triangle.
                    if(!changed)
                    {
                        retained.Clear();retained.Add(new GlassBarycentricTriangle(
                            new GlassBarycentric(1,0,0,1),new GlassBarycentric(0,1,0,1),new GlassBarycentric(0,0,1,1)));
                    }
                    emitted=checked(emitted+retained.Count);
                    if(emitted>limits.MaximumFragments)throw new WorkLimit();
                    output.Add(new GlassTriangleRemainder(ordinal,changed,retained));
                }
                result=new GlassCapClipResult(output);return true;
            }
            catch(WorkLimit){CapQueryBudgetFailures++;return false;}
            catch(System.OverflowException){CapQueryBudgetFailures++;return false;}
        }
        private List<List<Point2>> CapFragments(Surface face,Budget budget)
        {
            var candidates=new List<Surface>();var seen=new HashSet<Surface>();
            // Use the same exact plane/cell index as Nearby, but charge each
            // discovered surface while enumerating rather than first allocating
            // a possibly large complete neighbourhood set.
            foreach(var cell in face.Cells)
            {
                budget.Step();
                if(!cells.TryGetValue(cell,out var bucket))continue;
                foreach(var other in bucket)
                {
                    budget.Step();
                    if(!seen.Add(other))continue;
                    if(other.Id==face.Id||other.Winding==face.Winding||!face.Overlaps(other))continue;
                    candidates.Add(other);if(candidates.Count>limits.MaximumCandidates)throw new WorkLimit();
                }
            }
            candidates.Sort((a,b)=>a.Id!=b.Id?a.Id.CompareTo(b.Id):a.Order.CompareTo(b.Order));
            var fragments=new List<List<Point2>>{new List<Point2>(face.Points)};
            foreach(var other in candidates)
            {
                var remainder=new List<List<Point2>>();
                foreach(var fragment in fragments)
                {
                    var inside=fragment;
                    for(int edge=0;edge<3&&inside.Count>=3;edge++)
                    {
                        Split(inside,other.Points[edge],other.Points[(edge+1)%3],other.Winding,budget,out var next,out var outside);
                        if(HasArea(outside,budget))
                        {remainder.Add(outside);if(remainder.Count>limits.MaximumFragments)throw new WorkLimit();}
                        inside=next;
                    }
                }
                fragments=remainder;if(fragments.Count==0)break;
            }
            return fragments;
        }
        private static GlassBarycentric CapWeights(Point2[] source,Point2 point,Number determinant,Budget budget)
        {
            Number a=Cross(source[1],source[2],point,budget)/determinant;
            Number b=Cross(source[2],source[0],point,budget)/determinant;
            Number c=Number.FromDouble(1)-a-b;
            if(a.Sign<0||b.Sign<0||c.Sign<0)throw new WorkLimit();
            BigInteger d=CapLcm(CapLcm(a.D,b.D),c.D);
            return new GlassBarycentric(a.N*(d/a.D),b.N*(d/b.D),c.N*(d/c.D),d);
        }
        private static BigInteger CapLcm(BigInteger a,BigInteger b)
        {
            BigInteger value=a/BigInteger.GreatestCommonDivisor(a,b)*b;
            if(!(value>>512).IsZero)throw new WorkLimit();return value;
        }
    }
}
