namespace StoneAge.Game.Party;

public sealed record PartySnapshot(Guid PartyId, long LeaderId, IReadOnlyList<long> MemberIds);

public enum PartyInviteResult : byte
{
    Success = 0,
    InvalidTarget = 1,
    NotLeader = 2,
    TargetAlreadyInParty = 3,
    PartyFull = 4,
    InviteAlreadyPending = 5
}

public enum PartyAnswerResult : byte
{
    Success = 0,
    InviteNotFound = 1,
    InviterUnavailable = 2,
    TargetAlreadyInParty = 3,
    PartyFull = 4
}

public sealed class PartyManager
{
    public const int MaxMembers = 5;
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromSeconds(60);

    private sealed class Party
    {
        public required Guid Id { get; init; }
        public required long LeaderId { get; set; }
        public List<long> Members { get; } = [];
    }

    private sealed record PendingInvite(long InviterId, long TargetId, DateTimeOffset ExpiresAt);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Party> _parties = [];
    private readonly Dictionary<long, Guid> _membership = [];
    private readonly Dictionary<(long InviterId, long TargetId), PendingInvite> _invites = [];

    public PartyInviteResult Invite(long inviterId, long targetId)
    {
        lock (_sync)
        {
            CleanupExpiredInvites();
            if (inviterId <= 0 || targetId <= 0 || inviterId == targetId)
                return PartyInviteResult.InvalidTarget;
            if (_membership.ContainsKey(targetId))
                return PartyInviteResult.TargetAlreadyInParty;

            if (_membership.TryGetValue(inviterId, out var partyId))
            {
                var party = _parties[partyId];
                if (party.LeaderId != inviterId)
                    return PartyInviteResult.NotLeader;
                if (party.Members.Count >= MaxMembers)
                    return PartyInviteResult.PartyFull;
            }

            var key = (inviterId, targetId);
            if (_invites.ContainsKey(key))
                return PartyInviteResult.InviteAlreadyPending;

            _invites[key] = new PendingInvite(inviterId, targetId, DateTimeOffset.UtcNow + InviteLifetime);
            return PartyInviteResult.Success;
        }
    }

    public PartyAnswerResult Answer(long inviterId, long targetId, bool accept, out PartySnapshot? snapshot)
    {
        snapshot = null;
        lock (_sync)
        {
            CleanupExpiredInvites();
            var key = (inviterId, targetId);
            if (!_invites.Remove(key))
                return PartyAnswerResult.InviteNotFound;
            if (!accept)
                return PartyAnswerResult.Success;
            if (_membership.ContainsKey(targetId))
                return PartyAnswerResult.TargetAlreadyInParty;

            Party party;
            if (_membership.TryGetValue(inviterId, out var partyId))
            {
                if (!_parties.TryGetValue(partyId, out party!) || party.LeaderId != inviterId)
                    return PartyAnswerResult.InviterUnavailable;
            }
            else
            {
                party = new Party { Id = Guid.NewGuid(), LeaderId = inviterId };
                party.Members.Add(inviterId);
                _parties[party.Id] = party;
                _membership[inviterId] = party.Id;
            }

            if (party.Members.Count >= MaxMembers)
                return PartyAnswerResult.PartyFull;

            party.Members.Add(targetId);
            _membership[targetId] = party.Id;
            RemoveInvitesFor(targetId);
            snapshot = Snapshot(party);
            return PartyAnswerResult.Success;
        }
    }

    public bool Leave(long characterId, out PartySnapshot? remaining, out IReadOnlyList<long> affectedMembers)
    {
        remaining = null;
        affectedMembers = Array.Empty<long>();
        lock (_sync)
        {
            RemoveInvitesFor(characterId);
            if (!_membership.Remove(characterId, out var partyId) || !_parties.TryGetValue(partyId, out var party))
                return false;

            var before = party.Members.ToArray();
            party.Members.Remove(characterId);
            if (party.Members.Count <= 1)
            {
                foreach (var member in party.Members)
                    _membership.Remove(member);
                _parties.Remove(party.Id);
                affectedMembers = before;
                return true;
            }

            if (party.LeaderId == characterId)
                party.LeaderId = party.Members[0];

            remaining = Snapshot(party);
            affectedMembers = before;
            return true;
        }
    }

    public PartySnapshot? GetParty(long characterId)
    {
        lock (_sync)
        {
            if (!_membership.TryGetValue(characterId, out var partyId) || !_parties.TryGetValue(partyId, out var party))
                return null;
            return Snapshot(party);
        }
    }

    private static PartySnapshot Snapshot(Party party)
        => new(party.Id, party.LeaderId, party.Members.ToArray());

    private void CleanupExpiredInvites()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _invites.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            _invites.Remove(key);
    }

    private void RemoveInvitesFor(long characterId)
    {
        foreach (var key in _invites.Keys.Where(x => x.InviterId == characterId || x.TargetId == characterId).ToArray())
            _invites.Remove(key);
    }
}
