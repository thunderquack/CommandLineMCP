using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommandLineMcp.Data.Migrations;

/// <inheritdoc />
public partial class _20261112122053_AddUserRoleId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "RoleId",
            table: "Users",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RoleId",
            table: "Users");
    }
}
