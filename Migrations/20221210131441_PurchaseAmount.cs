using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyReferral.Migrations
{
    public partial class PurchaseAmount : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PurchaseAmount",
                table: "Referrals",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PurchaseAmount",
                table: "Referrals");
        }
    }
}
