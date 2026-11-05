using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace GraphicalTweaks
{
    [BepInPlugin(Id, "Sal's Graphical Tweaks", "2.0.1")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.sevo.graphicaltweaks";
        internal static Plugin Instance;
        [NonSerialized] private IPluginRuntime runtime;
        private bool initialized, started;
        internal ManualLogSource Log => Logger;
        internal ConfigFile Settings => Config;
        internal void LogDecal(string message) => Logger.LogInfo(message);

        private void Awake()
        {
            Instance = this;
            // Log before resolving optional game/rendering state.
            Logger.LogInfo("Unity 6 bootstrap entered (development build).");
            try
            {
                runtime = RuntimeFactory.Create(this);
                runtime.Initialize();
                initialized = true;
            }
            catch (Exception error) { Logger.LogError("Runtime initialization failed: " + error); }
        }

        // A trivial Start callback reproduces the creation crash in this
        // loader/player combination. Defer that stage to the first Update.
        private void Update()
        {
            if (!initialized) return;
            if (!started)
            {
                started = true;
                runtime.Start();
            }
            runtime.Tick();
        }
        private void OnEnable() { if (initialized) runtime.OnEnable(); }
        private void OnDisable() { if (initialized) runtime.OnDisable(); }
        private void OnDestroy()
        {
            try { runtime?.Dispose(); }
            finally { runtime = null; Instance = null; }
        }
    }

    internal interface IPluginRuntime : IDisposable
    {
        void Initialize();
        void Start();
        void Tick();
        void OnEnable();
        void OnDisable();
    }
}
