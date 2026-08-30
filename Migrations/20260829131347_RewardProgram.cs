using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyReferral.Migrations
{
    /// <inheritdoc />
    public partial class RewardProgram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Locale",
                table: "Referrals",
                type: "varchar(35)",
                maxLength: 35,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ProgramVersion",
                table: "Referrals",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RewardLedger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Reference = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RewardAccountId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: true),
                    RemunerationEurCents = table.Column<long>(type: "bigint", nullable: false),
                    PayoutThresholdEurCents = table.Column<long>(type: "bigint", nullable: true),
                    RelatedEntryId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    OfferVersion = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClaimCodeHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    WasAnonymous = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedBy = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DetailsJson = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RewardLedger", x => x.Id);
                    table.CheckConstraint("CK_RewardLedger_Claim", "(NOT `WasAnonymous` AND `RewardAccountId` IS NOT NULL AND `ClaimCodeHash` IS NULL AND `ClaimedAt` IS NULL) OR (`WasAnonymous` AND ((`RewardAccountId` IS NULL AND `ClaimCodeHash` IS NOT NULL AND `ClaimedAt` IS NULL) OR (`RewardAccountId` IS NOT NULL AND `ClaimCodeHash` IS NULL AND `ClaimedAt` IS NOT NULL)))");
                    table.CheckConstraint("CK_RewardLedger_Kind", "`Kind` BETWEEN 1 AND 6");
                    table.CheckConstraint("CK_RewardLedger_Source", "`Source` IS NULL OR `Source` BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_RewardLedger_RewardLedger_RelatedEntryId",
                        column: x => x.RelatedEntryId,
                        principalTable: "RewardLedger",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedger_ClaimCodeHash",
                table: "RewardLedger",
                column: "ClaimCodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedger_Reference",
                table: "RewardLedger",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedger_RelatedEntryId",
                table: "RewardLedger",
                column: "RelatedEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_RewardLedger_RewardAccountId_CreatedAt",
                table: "RewardLedger",
                columns: new[] { "RewardAccountId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RewardLedger");

            migrationBuilder.DropColumn(
                name: "Locale",
                table: "Referrals");

            migrationBuilder.DropColumn(
                name: "ProgramVersion",
                table: "Referrals");

        }
    }
}
