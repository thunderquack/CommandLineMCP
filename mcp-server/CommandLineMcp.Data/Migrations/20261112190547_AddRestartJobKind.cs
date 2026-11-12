using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommandLineMcp.Data.Migrations;

/// <inheritdoc />
public partial class _20261112190547_AddRestartJobKind : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Kind",
            table: "Jobs",
            type: "TEXT",
            nullable: false,
            defaultValue: "Command");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Kind",
            table: "Jobs");
    }
}
