using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommandLineMcp.Data.Migrations;

/// <inheritdoc />
public partial class _20261112111528_AddUsersClientsJobs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TelegramId = table.Column<long>(type: "INTEGER", nullable: false),
                IsOAuthAllowed = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Clients",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                BearerKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Clients", x => x.Id);
                table.ForeignKey(
                    name: "FK_Clients_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Jobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                Command = table.Column<string>(type: "TEXT", nullable: true),
                WorkingDirectory = table.Column<string>(type: "TEXT", nullable: true),
                TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "Pending"),
                Stdout = table.Column<string>(type: "TEXT", nullable: true),
                Stderr = table.Column<string>(type: "TEXT", nullable: true),
                ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Jobs", x => x.Id);
                table.CheckConstraint("CK_Jobs_Status", "\"Status\" IN ('Pending', 'Running', 'Succeeded', 'Failed', 'TimedOut', 'Unknown', 'Cancelled')");
                table.CheckConstraint("CK_Jobs_TimeoutSeconds_Positive", "\"TimeoutSeconds\" > 0");
                table.ForeignKey(
                    name: "FK_Jobs_Clients_ClientId",
                    column: x => x.ClientId,
                    principalTable: "Clients",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Clients_BearerKeyHash",
            table: "Clients",
            column: "BearerKeyHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Clients_UserId",
            table: "Clients",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Jobs_ClientId_Status_CreatedAt",
            table: "Jobs",
            columns: new[] { "ClientId", "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Users_TelegramId",
            table: "Users",
            column: "TelegramId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Jobs");

        migrationBuilder.DropTable(
            name: "Clients");

        migrationBuilder.DropTable(
            name: "Users");
    }
}
