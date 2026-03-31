using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRowVersionUseXmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "BankTransactions");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "JournalEntries",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "BankTransactions",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "BankTransactions");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "JournalEntries",
                type: "bytea",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "BankTransactions",
                type: "bytea",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
