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
    public long? CharacterId { get; private set; }
    public SessionState State { get; private set; } = SessionState.Connected;
    public DateTimeOffset LastActivityAt { get; private set; } = DateTimeOffset.UtcNow;

    public bool IsAuthenticated => State >= SessionState.Authenticated;

    public void Touch() => LastActivityAt = DateTimeOffset.UtcNow;

    public bool Authenticate(long accountId)
    {
        if (State != SessionState.Connected)
            return false;

        AccountId = accountId;
        State = SessionState.Authenticated;
        Touch();
        return true;
    }

    public bool SelectCharacter(long characterId)
    {
        if (State != SessionState.Authenticated)
            return false;

        CharacterId = characterId;
        State = SessionState.CharacterSelected;
        Touch();
        return true;
    }

    public bool EnterWorld()
    {
        if (State != SessionState.CharacterSelected || CharacterId is null)
            return false;

        State = SessionState.InWorld;
        Touch();
        return true;
    }
}
