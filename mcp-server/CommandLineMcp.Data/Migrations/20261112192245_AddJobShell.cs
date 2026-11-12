using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommandLineMcp.Data.Migrations;

/// <inheritdoc />
public partial class _20261112192245_AddJobShell : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Shell",
            table: "Jobs",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Shell",
            table: "Jobs");
    }
}
