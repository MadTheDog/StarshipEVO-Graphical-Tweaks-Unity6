using System;
using UnityEngine;

namespace GraphicalTweaks
{
    // Unity 2019 has no generic MaterialPropertyBlock.CopyFrom. Renderer stores
    // and retrieves a complete native copy, including properties unknown to us.
    // This disabled component has no mesh and never contributes to the scene.
    internal sealed class MaterialPropertyBlockCopy : IDisposable
    {
        private GameObject owner;
        private MeshRenderer storage;

        internal void Copy(MaterialPropertyBlock source, MaterialPropertyBlock destination, MaterialPropertyBlock second=null)
        {
            if(source==null||destination==null)throw new ArgumentNullException();
            if(storage==null)
            {
                owner=new GameObject("GT property block storage"){hideFlags=HideFlags.HideAndDontSave};
                owner.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(owner);
                storage=owner.AddComponent<MeshRenderer>();storage.enabled=false;
            }
            try
            {
                storage.SetPropertyBlock(source);
                storage.GetPropertyBlock(destination);
                if(second!=null)storage.GetPropertyBlock(second);
            }
            finally{storage.SetPropertyBlock(null);}
        }
        public void Dispose()
        {
            if(owner!=null)UnityEngine.Object.Destroy(owner);
            storage=null;owner=null;
        }
    }
}
