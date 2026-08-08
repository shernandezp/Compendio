using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compendio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiUsage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<string>(type: "TEXT", maxLength: 28, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Feature = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    InputCharacters = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsage", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_At",
                table: "AiUsage",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_UserId_At",
                table: "AiUsage",
                columns: new[] { "UserId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsage");
        }
    }
}
