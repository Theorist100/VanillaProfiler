using VanillaProfiler.Diagnostics;
using Game;

namespace VanillaProfiler
{
    /// <summary>
    /// Owns player-session read state separately from the recorder machinery.
    /// Loading/menu transitions clear public data immediately so exports and
    /// overlay reads cannot observe a previous city's snapshot.
    /// </summary>
    public sealed class ProfilerSessionState
    {
        public OverlaySnapshot? LastSnapshot { get; private set; }
        public HealthReport? LastHealth { get; private set; }
        public ProfilerLifecycleState LifecycleState { get; private set; } = ProfilerLifecycleState.Initializing;

        public bool IsGameLoaded => LifecycleState == ProfilerLifecycleState.Settling
            || LifecycleState == ProfilerLifecycleState.Active;
        public bool IsLoading => LifecycleState == ProfilerLifecycleState.LoadingCity;
        public bool IsSettling => LifecycleState == ProfilerLifecycleState.Settling;

        public bool Initialize(GameMode current)
        {
            var next = current == GameMode.Game
                ? ProfilerLifecycleState.Settling
                : ProfilerLifecycleState.NoCity;
            if (LifecycleState == next) return false;
            LifecycleState = next;
            ClearReadState();
            return true;
        }

        public bool BeginLoading()
        {
            if (LifecycleState == ProfilerLifecycleState.LoadingCity) return false;
            LifecycleState = ProfilerLifecycleState.LoadingCity;
            ClearReadState();
            return true;
        }

        public bool SetGameLoaded(bool gameLoaded)
        {
            var next = gameLoaded ? ProfilerLifecycleState.Settling : ProfilerLifecycleState.NoCity;
            if (LifecycleState == next) return false;
            LifecycleState = next;
            ClearReadState();
            return true;
        }

        public void Publish(OverlaySnapshot snapshot, HealthReport health)
        {
            LastSnapshot = snapshot;
            LastHealth = health;
            LifecycleState = ProfilerLifecycleState.Active;
        }

        public void ClearReadState()
        {
            LastSnapshot = null;
            LastHealth = null;
        }
    }
}
