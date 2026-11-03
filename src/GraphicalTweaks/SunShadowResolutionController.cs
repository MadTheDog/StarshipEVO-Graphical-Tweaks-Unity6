using UnityEngine;
using UnityEngine.Rendering;

namespace GraphicalTweaks
{
    internal sealed class SunShadowResolutionController
    {
        private readonly ShadowResolutionModel model;
        private Light current;
        private LightShadowResolution originalResolution;
        private int originalCustomResolution;
        private bool applied;

        internal SunShadowResolutionController(ShadowResolutionModel model) { this.model = model; }

        internal bool Apply(Light sun)
        {
            if (current != sun)
            {
                Restore();
                current = sun;
            }
            if (current == null) return false;
            if (!applied)
            {
                // While following the game, capture its latest untouched state.
                originalResolution = current.shadowResolution;
                originalCustomResolution = current.shadowCustomResolution;
            }
            int? selected = model.RequestedLightResolution;
            if (!selected.HasValue)
            {
                if (!applied) return false;
                RestoreValues();
                return true;
            }
            var requested = (LightShadowResolution)selected.Value;
            int pixels = model.RequestedPixelResolution.Value;
            bool changed = current.shadowResolution != requested || current.shadowCustomResolution != pixels;
            if (current.shadowResolution != requested) current.shadowResolution = requested;
            if (current.shadowCustomResolution != pixels) current.shadowCustomResolution = pixels;
            applied = true;
            return changed;
        }

        private void RestoreValues()
        {
            if (current != null)
            {
                current.shadowResolution = originalResolution;
                current.shadowCustomResolution = originalCustomResolution;
            }
            applied = false;
        }

        internal void Restore()
        {
            if (applied) RestoreValues();
            current = null;
        }
    }
}
