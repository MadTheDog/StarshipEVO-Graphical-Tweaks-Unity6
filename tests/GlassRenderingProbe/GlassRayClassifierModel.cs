using System;

namespace SEVO.GlassRenderingProbe
{
    // Original numeric helpers; no game state, rendering or physics dependencies.
    internal static class GlassRayClassifierModel
    {
        internal const int Opaque = 1, NonCaster = 2, Cutout = 4, Unknown = 8;

        internal readonly struct Point
        {
            internal readonly float X, Y, Z;
            internal Point(float x, float y, float z) { X=x;Y=y;Z=z; }
        }

        internal static int AlphaFlags(int alpha)
        {
            switch(alpha)
            {
                case 0: case 3: case 4: case 6: return Opaque;
                case 1: case 14: return Cutout;
                case 5: return Opaque|Cutout|NonCaster;
                case 2: case 7: case 8: case 9: case 10: case 11: case 12:
                case 13: case 15: case 16: case 17: case 18: case 19:
                case 20: case 21: return NonCaster;
                default: return Unknown;
            }
        }

        internal static bool Finite(float value) => !float.IsNaN(value)&&!float.IsInfinity(value);

        // Direction is local displacement per WORLD unit of ray distance.
        // Thus inverse rotation / entity scale preserves the distance parameter.
        // The point must already be inside/on the hit brick's geometry bounds.
        internal static bool TryExitBounds(Point point,Point direction,Point minimum,Point maximum,
            float tolerance,out float distance)
        {
            distance=0;
            if(!Finite(tolerance)||tolerance<0)return false;
            double exit=double.PositiveInfinity;
            if(!Axis(point.X,direction.X,minimum.X,maximum.X,tolerance,ref exit)||
               !Axis(point.Y,direction.Y,minimum.Y,maximum.Y,tolerance,ref exit)||
               !Axis(point.Z,direction.Z,minimum.Z,maximum.Z,tolerance,ref exit)||
               double.IsInfinity(exit)||double.IsNaN(exit)||exit>float.MaxValue)return false;
            distance=(float)exit;return true;
        }

        private static bool Axis(float point,float direction,float minimum,float maximum,float tolerance,ref double exit)
        {
            if(!Finite(point)||!Finite(direction)||!Finite(minimum)||!Finite(maximum)||minimum>maximum||
               point<(double)minimum-tolerance||point>(double)maximum+tolerance)return false;
            if(direction==0)return true;
            double endpoint=direction>0?maximum:minimum;
            double candidate=Math.Max(0,(endpoint-point)/direction);
            exit=Math.Min(exit,candidate);return true;
        }
    }
}
