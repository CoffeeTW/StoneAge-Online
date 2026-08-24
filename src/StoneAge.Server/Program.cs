using Microsoft.EntityFrameworkCore;
using StoneAge.Domain.Entities;
using StoneAge.Game.Item;
using StoneAge.Game.Npc;
using StoneAge.Game.World;
using StoneAge.Infrastructure.Persistence;
using StoneAge.Network.Server;
using StoneAge.Server;
using StoneAge.Server.Network;
using StoneAge.Server.Security;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("GameDatabase")
    ?? throw new InvalidOperationException("ConnectionStrings:GameDatabase is missing.");

builder.Services.AddDbContextFactory<GameDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<WorldManager>();
builder.Services.AddSingleton<NpcManager>();
builder.Services.AddSingleton(_ => ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "items", "items.json")));
builder.Services.AddSingleton<WorldConnectionRegistry>();
builder.Services.AddSingleton<LoginPacketHandler>();
builder.Services.AddSingleton<CharacterPacketHandler>();
builder.Services.AddSingleton<WorldPacketHandler>();
builder.Services.AddSingleton<NpcPacketHandler>();
builder.Services.AddSingleton<InventoryShopPacketHandler>();
builder.Services.AddSingleton<ItemEquipmentPacketHandler>();
builder.Services.AddSingleton<IClientPacketHandler, CompositePacketHandler>();
builder.Services.AddSingleton<TcpGameServer>();
builder.Services.AddHostedService<GameServerWorker>();
builder.Services.AddHostedService<WorldAutosaveWorker>();

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GameDbContext>>();
    var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await DatabaseSchemaUpgrade.ApplyAsync(db);

    if (!await db.Accounts.AnyAsync(x => x.Username == "test"))
    {
        db.Accounts.Add(new Account
        {
            Username = "test",
            PasswordHash = hasher.Hash("test1234"),
            Status = 0
        });
        await db.SaveChangesAsync();
    }
}

await host.RunAsync();
