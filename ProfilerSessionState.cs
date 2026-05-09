using Game;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using VanillaProfiler.Diagnostics;

namespace VanillaProfiler
{
    [SuppressMessage("Usage", "CA1815:Override equals and operator equals on value types", Justification = "Lifecycle tokens are data carriers; equality is not part of the contract.")]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct ProfilerSessionToken
    {
        public static readonly ProfilerSessionToken Initial =
            new(0, 0, ProfilerLifecycleState.Initializing);

        public ProfilerSessionToken(long sessionId, long loadId, ProfilerLifecycleState state)
        {
            SessionId = sessionId;
            LoadId = loadId;
            State = state;
        }

        public readonly long SessionId;
        public readonly long LoadId;
        public readonly ProfilerLifecycleState State;
    }

    [SuppressMessage("Usage", "CA1815:Override equals and operator equals on value types", Justification = "Transitions are single-use return values; equality is not part of the contract.")]
    [StructLayout(LayoutKind.Auto)]
    public readonly struct ProfilerLifecycleTransition
    {
        public ProfilerLifecycleTransition(
            ProfilerSessionToken token,
            ProfilerLifecycleState previousState,
            ProfilerLifecycleState nextState,
            SessionBoundary? boundary,
            bool isDuplicate,
            bool isSyntheticLoadStart)
        {
            Token = token;
            PreviousState = previousState;
            NextState = nextState;
            Boundary = boundary;
            IsDuplicate = isDuplicate;
            IsSyntheticLoadStart = isSyntheticLoadStart;
        }

        public readonly ProfilerSessionToken Token;
        public readonly ProfilerLifecycleState PreviousState;
        public readonly ProfilerLifecycleState NextState;
        public readonly SessionBoundary? Boundary;
        public readonly bool IsDuplicate;
        public readonly bool IsSyntheticLoadStart;
    }

    /// <summary>
    /// Owns player-session read state and lifecycle tokens. Loading/menu transitions
    /// clear public data immediately so exports and overlay reads cannot observe a
    /// previous city's snapshot.
    /// </summary>
    public sealed class ProfilerSessionState
    {
        private ProfilerSessionToken m_Token = ProfilerSessionToken.Initial;

        public OverlaySnapshot? LastSnapshot { get; private set; }
        public HealthReport? LastHealth { get; private set; }
        public ProfilerSessionToken Token => m_Token;
        public ProfilerLifecycleState LifecycleState => m_Token.State;

        public bool IsGameLoaded => LifecycleState == ProfilerLifecycleState.Settling
            || LifecycleState == ProfilerLifecycleState.Active;
        public bool IsLoading => LifecycleState == ProfilerLifecycleState.LoadingCity;
        public bool IsSettling => LifecycleState == ProfilerLifecycleState.Settling;

        public ProfilerLifecycleTransition Initialize(GameMode current)
        {
            return current == GameMode.Game
                ? StartLoadedCity(isSyntheticLoadStart: false)
                : LeaveCity();
        }

        public ProfilerLifecycleTransition BeginLoading(bool loadsCity)
        {
            if (!loadsCity) return LeaveCity();

            if (LifecycleState == ProfilerLifecycleState.LoadingCity)
                return Duplicate();

            return BeginCityLoad();
        }

        public ProfilerLifecycleTransition CompleteLoad(GameMode mode)
        {
            return mode == GameMode.Game
                ? CompleteCityLoad()
                : LeaveCity();
        }

        public ProfilerLifecycleTransition SetGameLoaded(bool gameLoaded)
        {
            return gameLoaded
                ? CompleteCityLoad()
                : LeaveCity();
        }

        public ProfilerLifecycleTransition Publish(OverlaySnapshot snapshot, HealthReport health)
        {
            LastSnapshot = snapshot;
            LastHealth = health;

            if (LifecycleState != ProfilerLifecycleState.Settling)
                return Duplicate();

            return MoveTo(ProfilerLifecycleState.Active, boundary: null);
        }

        public void ClearReadState()
        {
            LastSnapshot = null;
            LastHealth = null;
        }

        private ProfilerLifecycleTransition BeginCityLoad()
        {
            return MoveTo(
                ProfilerLifecycleState.LoadingCity,
                SessionBoundary.BeginLoading,
                sessionId: m_Token.SessionId,
                loadId: m_Token.LoadId + 1);
        }

        private ProfilerLifecycleTransition CompleteCityLoad()
        {
            if (LifecycleState == ProfilerLifecycleState.LoadingCity)
                return StartLoadedCity(isSyntheticLoadStart: false);

            if (LifecycleState == ProfilerLifecycleState.Settling)
                return Duplicate();

            return BeginSyntheticLoadedCity();
        }

        private ProfilerLifecycleTransition BeginSyntheticLoadedCity()
        {
            return MoveTo(
                ProfilerLifecycleState.Settling,
                SessionBoundary.GameLoaded,
                sessionId: m_Token.SessionId + 1,
                loadId: m_Token.LoadId + 1,
                isSyntheticLoadStart: true);
        }

        private ProfilerLifecycleTransition StartLoadedCity(bool isSyntheticLoadStart)
        {
            long loadId = LifecycleState == ProfilerLifecycleState.LoadingCity
                ? m_Token.LoadId
                : m_Token.LoadId + 1;

            return MoveTo(
                ProfilerLifecycleState.Settling,
                SessionBoundary.GameLoaded,
                sessionId: m_Token.SessionId + 1,
                loadId: loadId,
                isSyntheticLoadStart: isSyntheticLoadStart);
        }

        private ProfilerLifecycleTransition LeaveCity()
        {
            if (LifecycleState == ProfilerLifecycleState.Initializing
                || LifecycleState == ProfilerLifecycleState.NoCity)
            {
                return MoveTo(ProfilerLifecycleState.NoCity, boundary: null);
            }

            return MoveTo(
                ProfilerLifecycleState.NoCity,
                SessionBoundary.GameUnloaded);
        }

        private ProfilerLifecycleTransition MoveTo(
            ProfilerLifecycleState nextState,
            SessionBoundary? boundary,
            long? sessionId = null,
            long? loadId = null,
            bool isSyntheticLoadStart = false)
        {
            var previous = LifecycleState;
            if (previous == nextState
                && boundary == null
                && sessionId.GetValueOrDefault(m_Token.SessionId) == m_Token.SessionId
                && loadId.GetValueOrDefault(m_Token.LoadId) == m_Token.LoadId)
            {
                return Duplicate();
            }

            ClearReadState();
            m_Token = new ProfilerSessionToken(
                sessionId.GetValueOrDefault(m_Token.SessionId),
                loadId.GetValueOrDefault(m_Token.LoadId),
                nextState);

            return new ProfilerLifecycleTransition(
                m_Token,
                previous,
                nextState,
                boundary,
                isDuplicate: false,
                isSyntheticLoadStart: isSyntheticLoadStart);
        }

        private ProfilerLifecycleTransition Duplicate()
        {
            return new ProfilerLifecycleTransition(
                m_Token,
                LifecycleState,
                LifecycleState,
                boundary: null,
                isDuplicate: true,
                isSyntheticLoadStart: false);
        }
    }
}
