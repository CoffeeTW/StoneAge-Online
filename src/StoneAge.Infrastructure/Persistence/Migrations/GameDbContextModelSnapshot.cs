using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace StoneAge.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GameDbContext))]
public sealed class GameDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");
        GameDbContext.ConfigureModel(modelBuilder);
    }
}
