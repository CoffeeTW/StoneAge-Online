using StoneAge.Network.Server;

namespace StoneAge.Server;

public sealed class GameServerWorker(
    TcpGameServer gameServer,
    IConfiguration configuration,
    ILogger<GameServerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = configuration.GetValue<int?>("Server:Port") ?? 7021;
        var name = configuration["Server:Name"] ?? "StoneAge Online";

        logger.LogInformation("{ServerName} starting", name);

        await gameServer.RunAsync(
            port,
            message => logger.LogInformation("{NetworkMessage}", message),
            stoppingToken);
    }
}
