using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrencySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "JournalEntries",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "ARS");

            migrationBuilder.AddColumn<string>(
                name: "UsdBankAccountName",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "BankTransactions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "ARS");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "AfipVouchers",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "ARS");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_CompanyId_Currency",
                table: "BankTransactions",
                columns: new[] { "CompanyId", "Currency" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_CompanyId_Currency",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "UsdBankAccountName",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "AfipVouchers");
        }
    }
}
