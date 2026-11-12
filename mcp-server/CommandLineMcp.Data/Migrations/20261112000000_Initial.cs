using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CommandLineMcp.Data.Migrations;

[DbContext(typeof(CommandLineMcpDbContext))]
[Migration("20261112000000_Initial")]
public sealed class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
