using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyAndRulesPerCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "BankTransactions",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "AccountingRules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Cuit = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    BusinessType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    StudioTenantId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_TenantId",
                table: "BankTransactions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingRules_CompanyId",
                table: "AccountingRules",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_Cuit",
                table: "Companies",
                column: "Cuit",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_StudioTenantId",
                table: "Companies",
                column: "StudioTenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_TenantId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AccountingRules_CompanyId",
                table: "AccountingRules");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "AccountingRules");

            migrationBuilder.AlterColumn<string>(
                name: "TenantId",
                table: "BankTransactions",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
