using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyReferral.Migrations
{
    /// <inheritdoc />
    public partial class CreatorOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreatorOnboardingReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreatorUserId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MinecraftUuid = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ResidenceCountry = table.Column<string>(type: "varchar(2)", maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TaxResidenceCountry = table.Column<string>(type: "varchar(2)", maxLength: 2, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SellerType = table.Column<int>(type: "int", nullable: false),
                    CapacityJurisdiction = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CapacityStatus = table.Column<int>(type: "int", nullable: false),
                    TaxDocumentRoute = table.Column<int>(type: "int", nullable: false),
                    PrivacyNoticeVersion = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VerificationReference = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RepresentativeAccountId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RepresentativeAgreementHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RepresentativeAcceptedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    EvidenceReference = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EvidenceSha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReviewedBy = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReviewedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RuleVersion = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ValidUntilUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PreviousReviewId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreatorOnboardingReviews", x => x.Id);
                    table.CheckConstraint("CK_CreatorOnboardingReviews_CapacityStatus", "`CapacityStatus` BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_CreatorOnboardingReviews_SellerType", "`SellerType` BETWEEN 1 AND 2");
                    table.CheckConstraint("CK_CreatorOnboardingReviews_Status", "`Status` BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_CreatorOnboardingReviews_TaxDocumentRoute", "`TaxDocumentRoute` BETWEEN 1 AND 5");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorOnboardingReviews_CreatorUserId_ReviewedAtUtc",
                table: "CreatorOnboardingReviews",
                columns: new[] { "CreatorUserId", "ReviewedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CreatorOnboardingReviews_CreatorUserId_PreviousReviewId",
                table: "CreatorOnboardingReviews",
                columns: new[] { "CreatorUserId", "PreviousReviewId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreatorOnboardingReviews");
        }
    }
}
