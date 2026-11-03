using System;
using SEVO.GlassRenderingProbe;
using Point=SEVO.GlassRenderingProbe.GlassRayClassifierModel.Point;

static class Program
{
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception(name);++checks;}
    static Point P(float x,float y=0,float z=0)=>new Point(x,y,z);
    static bool Exit(Point p,Point d,out float t,float tolerance=0)=>GlassRayClassifierModel.TryExitBounds(p,d,P(-1,-1,-1),P(1,1,1),tolerance,out t);
    static void Main()
    {
        Check(Exit(P(0),P(1),out var t)&&t==1,"positive X exit");
        Check(Exit(P(0.5f),P(-1),out t)&&t==1.5f,"negative X exit");
        Check(Exit(P(0),P(0,1),out t)&&t==1,"parallel X/Z slabs");
        Check(Exit(P(0),P(0,0,-1),out t)&&t==1,"negative Z exit");
        Check(Exit(P(0),P(4),out t)&&t==0.25f,"quarter-scale entity uses world distance");
        Check(Exit(P(0),P(0.25f),out t)&&t==4,"four-times-scale entity uses world distance");
        Check(Exit(P(0),P(1,2,3),out t)&&Math.Abs(t-1f/3)<1e-7f,"nearest exit wins");
        Check(Exit(P(1),P(1),out t)&&t==0,"outward boundary zero exit");
        Check(Exit(P(1),P(-1),out t)&&t==2,"inward boundary opposite exit");
        Check(!Exit(P(2),P(-1),out t),"outside origin is not a later entry");
        Check(Exit(P(1.0001f),P(1),out t,0.001f)&&t==0,"hit roundoff tolerance");
        Check(!Exit(P(1.01f),P(1),out t,0.001f),"large mismatch is ambiguous");
        Check(!Exit(P(0),P(0),out t),"zero direction rejected");
        Check(Exit(P(0),P(float.Epsilon,1),out t)&&t==1,"nearly parallel direction remains stable");
        Check(!Exit(P(float.NaN),P(1),out t),"NaN point rejected");
        Check(!Exit(P(0),P(float.PositiveInfinity),out t),"infinite direction rejected");
        Check(!GlassRayClassifierModel.TryExitBounds(P(0),P(1),P(1),P(-1),0,out t),"inverted bounds rejected");
        Check(GlassRayClassifierModel.TryExitBounds(P(0),P(1),P(0,0,0),P(0,1,1),0,out t)&&t==0,"zero thickness boundary");
        for(int alpha=0;alpha<22;++alpha)
        {
            int expected=alpha==0||alpha==3||alpha==4||alpha==6?1:(alpha==1||alpha==14?4:(alpha==5?7:2));
            Check(GlassRayClassifierModel.AlphaFlags(alpha)==expected,"material alpha mode "+alpha);
        }
        Check((GlassRayClassifierModel.AlphaFlags(0)|GlassRayClassifierModel.AlphaFlags(2))==3,"opaque plus glass stays mixed");
        Check(GlassRayClassifierModel.AlphaFlags(999)==8,"unknown alpha mode ambiguous");
        Console.WriteLine("PASS "+checks+" ray classifier geometry/material checks");
    }
}
