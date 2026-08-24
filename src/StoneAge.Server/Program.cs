using Microsoft.EntityFrameworkCore;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Server;
using StoneAge.Server;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("GameDatabase")
    ?? throw new InvalidOperationException("ConnectionStrings:GameDatabase is missing.");

builder.Services.AddDbContextFactory<GameDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<TcpGameServer>();
builder.Services.AddHostedService<GameServerWorker>();

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GameDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

await host.RunAsync();
