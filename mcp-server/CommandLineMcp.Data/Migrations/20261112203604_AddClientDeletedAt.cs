using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommandLineMcp.Data.Migrations;

/// <inheritdoc />
public partial class _20261112203604_AddClientDeletedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "DeletedAt",
            table: "Clients",
            type: "TEXT",
            nullable: true);

        // Ранее отключённые клиенты должны остаться неактивными после миграции.
        // В старой схеме дата отзыва не сохранялась.
        migrationBuilder.Sql("""
            UPDATE "Clients"
            SET "DeletedAt" = strftime('%Y-%m-%d %H:%M:%f', 'now')
            WHERE "IsEnabled" = 0 AND "DeletedAt" IS NULL;
            """);

        migrationBuilder.DropColumn(
            name: "IsEnabled",
            table: "Clients");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsEnabled",
            table: "Clients",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.Sql("""
            UPDATE "Clients" SET "IsEnabled" = 0 WHERE "DeletedAt" IS NOT NULL;
            """);

        migrationBuilder.DropColumn(
            name: "DeletedAt",
            table: "Clients");
    }
}
