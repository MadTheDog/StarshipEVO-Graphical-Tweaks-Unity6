using System.Globalization;
using UnityEngine;

namespace GraphicalTweaks
{
    internal static class ObjectIdentity
    {
        internal static string Describe(Object value)
        {
#if SEVO_UNITY6
            return EntityId.ToULong(value.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return value.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }
    }
}
