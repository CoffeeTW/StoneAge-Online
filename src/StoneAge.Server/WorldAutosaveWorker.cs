using Microsoft.EntityFrameworkCore;
using StoneAge.Game.World;
using StoneAge.Infrastructure.Persistence;

namespace StoneAge.Server;

public sealed class WorldAutosaveWorker(
    WorldManager world,
    IDbContextFactory<GameDbContext> dbFactory,
    ILogger<WorldAutosaveWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SaveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "World autosave failed");
            }
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var players = world.GetAllPlayers().ToArray();
        if (players.Length == 0)
            return;

        var ids = players.Select(x => x.CharacterId).ToArray();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var characters = await db.Characters
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var player in players)
        {
            if (!characters.TryGetValue(player.CharacterId, out var character))
                continue;

            character.MapId = player.MapId;
            character.X = player.X;
            character.Y = player.Y;
            character.Direction = player.Direction;
            character.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Autosaved {PlayerCount} online players", characters.Count);
    }
}
