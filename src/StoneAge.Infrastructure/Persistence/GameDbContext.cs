using Microsoft.EntityFrameworkCore;
using StoneAge.Domain.Entities;

namespace StoneAge.Infrastructure.Persistence;

public sealed class GameDbContext(DbContextOptions<GameDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<CharacterItem> CharacterItems => Set<CharacterItem>();
    public DbSet<CharacterPet> CharacterPets => Set<CharacterPet>();
    public DbSet<CharacterPetSkill> CharacterPetSkills => Set<CharacterPetSkill>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => ConfigureModel(modelBuilder);

    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Username).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => x.Username).IsUnique();
            entity.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<Character>(entity =>
        {
            entity.ToTable("characters");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(24).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasOne(x => x.Account)
                .WithMany(x => x.Characters)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CharacterItem>(entity =>
        {
            entity.ToTable("character_items");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CharacterId, x.ItemId });
            entity.HasIndex(x => new { x.CharacterId, x.Slot }).IsUnique();
            entity.HasIndex(x => new { x.CharacterId, x.EquippedSlot })
                .IsUnique()
                .HasFilter("\"EquippedSlot\" IS NOT NULL");
            entity.Property(x => x.Quantity).IsRequired();
            entity.Property(x => x.Slot).IsRequired();
            entity.HasOne(x => x.Character)
                .WithMany(x => x.Inventory)
                .HasForeignKey(x => x.CharacterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CharacterPet>(entity =>
        {
            entity.ToTable("character_pets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(24).IsRequired();
            entity.HasIndex(x => new { x.CharacterId, x.IsActive })
                .IsUnique()
                .HasFilter("\"IsActive\" = TRUE");
            entity.HasOne(x => x.Character)
                .WithMany(x => x.Pets)
                .HasForeignKey(x => x.CharacterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CharacterPetSkill>(entity =>
        {
            entity.ToTable("character_pet_skills");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CharacterPetId, x.Slot }).IsUnique();
            entity.Property(x => x.Slot).IsRequired();
            entity.Property(x => x.SkillId).IsRequired();
            entity.HasOne(x => x.Pet)
                .WithMany(x => x.Skills)
                .HasForeignKey(x => x.CharacterPetId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
