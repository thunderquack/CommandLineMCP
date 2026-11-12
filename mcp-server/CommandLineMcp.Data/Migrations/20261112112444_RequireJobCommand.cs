using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommandLineMcp.Data.Migrations;

/// <inheritdoc />
public partial class _20261112112444_RequireJobCommand : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TRIGGER "__RequireJobCommandGuard"
            BEFORE UPDATE ON main."Jobs"
            WHEN OLD."Command" IS NULL
            BEGIN
                SELECT RAISE(ABORT, 'Cannot require Jobs.Command while NULL command values exist.');
            END;
            """);

        migrationBuilder.Sql("UPDATE \"Jobs\" SET \"Command\" = \"Command\";");
        migrationBuilder.Sql("DROP TRIGGER \"__RequireJobCommandGuard\";");

        migrationBuilder.AlterColumn<string>(
            name: "Command",
            table: "Jobs",
            type: "TEXT",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Command",
            table: "Jobs",
            type: "TEXT",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT");
    }
}
