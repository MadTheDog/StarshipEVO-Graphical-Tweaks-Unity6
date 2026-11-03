// Pure adapter fixture: native Unity setter behavior remains a separate runtime gate.
namespace UnityEngine
{
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
    }
    public static class QualitySettings
    {
        public static int shadowCascades;
        public static float shadowDistance;
        public static int TwoWrites,FourWrites;
        private static float two;
        private static Vector3 four;
        public static float shadowCascade2Split {get=>two;set{two=value;TwoWrites++;}}
        public static Vector3 shadowCascade4Split {get=>four;set{four=value;FourWrites++;}}
    }
}
