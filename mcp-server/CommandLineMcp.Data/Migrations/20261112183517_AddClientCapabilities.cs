using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommandLineMcp.Data.Migrations;

/// <inheritdoc />
public partial class _20261112183517_AddClientCapabilities : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Architecture",
            table: "Clients",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CapabilitiesUpdatedAt",
            table: "Clients",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DefaultShell",
            table: "Clients",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OperatingSystem",
            table: "Clients",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OsVersion",
            table: "Clients",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ShellsJson",
            table: "Clients",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Architecture",
            table: "Clients");

        migrationBuilder.DropColumn(
            name: "CapabilitiesUpdatedAt",
            table: "Clients");

        migrationBuilder.DropColumn(
            name: "DefaultShell",
            table: "Clients");

        migrationBuilder.DropColumn(
            name: "OperatingSystem",
            table: "Clients");

        migrationBuilder.DropColumn(
            name: "OsVersion",
            table: "Clients");

        migrationBuilder.DropColumn(
            name: "ShellsJson",
            table: "Clients");
    }
}
