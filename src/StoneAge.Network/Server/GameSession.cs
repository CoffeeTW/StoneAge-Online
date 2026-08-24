namespace StoneAge.Network.Server;

public enum SessionState : byte
{
    Connected = 0,
    Authenticated = 1,
    CharacterSelected = 2,
    InWorld = 3
}

public sealed class GameSession
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public long? AccountId { get; private set; }
    public SessionState State { get; private set; } = SessionState.Connected;

    public bool IsAuthenticated => State >= SessionState.Authenticated;

    public bool Authenticate(long accountId)
    {
        if (State != SessionState.Connected)
            return false;

        AccountId = accountId;
        State = SessionState.Authenticated;
        return true;
    }
}
