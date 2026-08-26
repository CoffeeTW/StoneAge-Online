using StoneAge.Game.Battle;

namespace StoneAge.Game.Tests;

public sealed class PartyBattleSessionTests
{
    [Fact]
    public void TrySubmitAction_WaitsForEveryLivingParticipant()
    {
        var session = CreateSession();

        var firstAccepted = session.TrySubmitAction(10, 1, out var firstResolution);
        var secondAccepted = session.TrySubmitAction(20, 2, out var secondResolution);

        Assert.True(firstAccepted);
        Assert.Null(firstResolution);
        Assert.True(secondAccepted);
        Assert.NotNull(secondResolution);
        Assert.Equal(1, secondResolution!.Turn);
    }

    [Fact]
    public void TrySubmitAction_RejectsDuplicateSubmissionInSameTurn()
    {
        var session = CreateSession();

        Assert.True(session.TrySubmitAction(10, 1, out var firstResolution));
        Assert.Null(firstResolution);
        Assert.False(session.TrySubmitAction(10, 2, out var duplicateResolution));
        Assert.Null(duplicateResolution);
    }

    [Fact]
    public void CanSubmitAction_ReturnsFalseForKnockedOutParticipant()
    {
        var session = CreateSession();
        session.Participants.Single(x => x.CharacterId == 20).CurrentHp = 0;

        Assert.False(session.CanSubmitAction(20));
        Assert.True(session.TrySubmitAction(10, 1, out var resolution));
        Assert.NotNull(resolution);
    }

    private static PartyBattleSession CreateSession()
    {
        var monster = new MonsterDefinition
        {
            Id = 9001,
            Name = "Test Beast",
            MaxHp = 500,
            Attack = 1,
            Defense = 1,
            Agility = 1,
            Earth = 25,
            Water = 25,
            Fire = 25,
            Wind = 25
        };
        var participants = new[]
        {
            new PartyBattleParticipant(10, "Leader", true, 100, 100, 10, 10, 10, 25, 25, 25, 25, null),
            new PartyBattleParticipant(20, "Member", false, 100, 100, 10, 10, 9, 25, 25, 25, 25, null)
        };
        return new PartyBattleSession(Guid.NewGuid(), monster, participants);
    }
}
