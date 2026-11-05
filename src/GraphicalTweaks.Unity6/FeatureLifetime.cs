using System;
using System.Collections.Generic;

namespace GraphicalTweaks
{
    internal sealed class FeatureLifetime : IDisposable
    {
        private readonly Action<string, Exception> report;
        private readonly HashSet<string> attempted = new HashSet<string>();
        private readonly List<KeyValuePair<string, Action>> active = new List<KeyValuePair<string, Action>>();
        private bool disposed;

        internal FeatureLifetime(Action<string, Exception> report)
        { this.report = report ?? throw new ArgumentNullException(nameof(report)); }

        internal bool TryStartNative(string name, Action validate, Action initialize, Action restore)
        {
            if (disposed || attempted.Contains(name)) return false;
            try { validate(); }
            catch (Exception error)
            {
                attempted.Add(name);
                Report(name, error);
                return false;
            }
            return TryStart(name, initialize, restore);
        }

        internal bool TryStart(string name, Action initialize, Action restore)
        {
            if (disposed || !attempted.Add(name)) return false;
            try
            {
                initialize();
                active.Add(new KeyValuePair<string, Action>(name, restore));
                return true;
            }
            catch (Exception error)
            {
                Report(name, error);
                try { restore(); }
                catch (Exception cleanup) { Report(name, cleanup); }
                return false;
            }
        }

        private void Report(string name, Exception error)
        {
            // Diagnostics must not break independent initialization/restoration.
            try { report(name, error); }
            catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                try { active[i].Value(); }
                catch (Exception error) { Report(active[i].Key, error); }
            }
            active.Clear();
        }
    }
}
