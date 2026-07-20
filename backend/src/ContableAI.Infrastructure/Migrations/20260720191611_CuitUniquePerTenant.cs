using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CuitUniquePerTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Companies_Cuit",
                table: "Companies");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_StudioTenantId_Cuit",
                table: "Companies",
                columns: new[] { "StudioTenantId", "Cuit" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Companies_StudioTenantId_Cuit",
                table: "Companies");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_Cuit",
                table: "Companies",
                column: "Cuit",
                unique: true);
        }
    }
}
