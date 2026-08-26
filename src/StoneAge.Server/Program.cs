using Microsoft.EntityFrameworkCore;
using StoneAge.Domain.Entities;
using StoneAge.Game.Battle;
using StoneAge.Game.Item;
using StoneAge.Game.Npc;
using StoneAge.Game.Party;
using StoneAge.Game.Pet;
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
builder.Services.AddSingleton<PartyManager>();
builder.Services.AddSingleton(_ => ItemCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "items", "items.json")));
builder.Services.AddSingleton(_ => MonsterCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "monsters", "monsters.json")));
builder.Services.AddSingleton(_ => PetSkillCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "pet-skills", "pet-skills.json")));
builder.Services.AddSingleton<BattleManager>();
builder.Services.AddSingleton<PartyBattleManager>();
builder.Services.AddSingleton<WorldConnectionRegistry>();
builder.Services.AddSingleton<LoginPacketHandler>();
builder.Services.AddSingleton<CharacterPacketHandler>();
builder.Services.AddSingleton<BattlePacketHandler>();
builder.Services.AddSingleton<PartyBattlePacketHandler>();
builder.Services.AddSingleton<WorldPacketHandler>();
builder.Services.AddSingleton<NpcPacketHandler>();
builder.Services.AddSingleton<InventoryShopPacketHandler>();
builder.Services.AddSingleton<ItemEquipmentPacketHandler>();
builder.Services.AddSingleton<PetPacketHandler>();
builder.Services.AddSingleton<PetSkillPacketHandler>();
builder.Services.AddSingleton<SocialPacketHandler>();
builder.Services.AddSingleton<IClientPacketHandler, CompositePacketHandler>();
builder.Services.AddSingleton<TcpGameServer>();
builder.Services.AddHostedService<GameServerWorker>();
builder.Services.AddHostedService<WorldAutosaveWorker>();

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GameDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await DatabaseSchemaUpgrade.ApplyAsync(db);

    if (builder.Environment.IsDevelopment())
    {
        var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
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
}

await host.RunAsync();
