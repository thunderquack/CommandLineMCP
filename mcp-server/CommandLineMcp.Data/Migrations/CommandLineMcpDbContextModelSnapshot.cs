using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CommandLineMcp.Data.Migrations;

[DbContext(typeof(CommandLineMcpDbContext))]
public sealed class CommandLineMcpDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "11.0.0");
    }
}
