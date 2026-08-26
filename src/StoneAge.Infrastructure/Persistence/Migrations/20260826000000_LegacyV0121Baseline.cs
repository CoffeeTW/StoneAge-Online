using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace StoneAge.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GameDbContext))]
[Migration(MigrationId)]
public sealed class LegacyV0121Baseline : Migration
{
    public const string MigrationId = "20260826000000_LegacyV0121Baseline";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The v0.1-21 schema already exists before this migration is recorded.
        // MigrationBootstrap aligns legacy databases first, then inserts this
        // baseline into __EFMigrationsHistory without recreating user data.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Baseline marker only. Downgrading must never drop the legacy schema.
    }
}
