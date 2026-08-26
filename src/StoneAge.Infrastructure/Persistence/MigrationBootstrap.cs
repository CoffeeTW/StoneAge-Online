using Microsoft.EntityFrameworkCore;
using StoneAge.Infrastructure.Persistence.Migrations;

namespace StoneAge.Infrastructure.Persistence;

public static class MigrationBootstrap
{
    private const string ProductVersion = "10.0.11";

    public static async Task ApplyAsync(GameDbContext db, CancellationToken cancellationToken = default)
    {
        // Transitional bridge for installations created before EF migrations.
        // Fresh databases are created from the current model; legacy databases
        // are aligned by the existing idempotent upgrader before the baseline
        // migration is recorded.
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await DatabaseSchemaUpgrade.ApplyAsync(db, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );

            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260826000000_LegacyV0121Baseline', '10.0.11')
            ON CONFLICT ("MigrationId") DO NOTHING;
            """, cancellationToken);

        // Applies migrations newer than the recorded legacy baseline. At
        // v0.1-22 there are none yet, so this is intentionally a no-op after
        // history bootstrap and establishes the path for future schema deltas.
        await db.Database.MigrateAsync(cancellationToken);
    }
}
