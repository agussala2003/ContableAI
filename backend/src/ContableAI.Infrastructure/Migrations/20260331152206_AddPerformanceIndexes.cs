using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_CompanyId_ClassificationSource",
                table: "BankTransactions",
                columns: new[] { "CompanyId", "ClassificationSource" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_CompanyId_Date",
                table: "BankTransactions",
                columns: new[] { "CompanyId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingRules_CompanyId_Priority",
                table: "AccountingRules",
                columns: new[] { "CompanyId", "Priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_CompanyId_ClassificationSource",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_CompanyId_Date",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_AccountingRules_CompanyId_Priority",
                table: "AccountingRules");
        }
    }
}
