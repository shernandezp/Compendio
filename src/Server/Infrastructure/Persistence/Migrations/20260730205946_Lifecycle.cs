using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compendio.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Lifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AcknowledgmentRounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpenedAt = table.Column<string>(type: "TEXT", maxLength: 28, nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcknowledgmentRounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Acknowledgments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PageVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<string>(type: "TEXT", maxLength: 28, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Acknowledgments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", maxLength: 28, nullable: false),
                    ReadAt = table.Column<string>(type: "TEXT", maxLength: 28, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pages_NextReviewDate",
                table: "Pages",
                column: "NextReviewDate");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_Owner",
                table: "Pages",
                column: "Owner");

            migrationBuilder.CreateIndex(
                name: "IX_AcknowledgmentRounds_PageId_OpenedAt",
                table: "AcknowledgmentRounds",
                columns: new[] { "PageId", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Acknowledgments_PageId",
                table: "Acknowledgments",
                column: "PageId");

            migrationBuilder.CreateIndex(
                name: "IX_Acknowledgments_PageId_UserId_PageVersionId",
                table: "Acknowledgments",
                columns: new[] { "PageId", "UserId", "PageVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Acknowledgments_UserId",
                table: "Acknowledgments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_Kind_TargetPath",
                table: "Notifications",
                columns: new[] { "UserId", "Kind", "TargetPath" },
                unique: true,
                filter: "\"ReadAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcknowledgmentRounds");

            migrationBuilder.DropTable(
                name: "Acknowledgments");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Pages_NextReviewDate",
                table: "Pages");

            migrationBuilder.DropIndex(
                name: "IX_Pages_Owner",
                table: "Pages");
        }
    }
}
