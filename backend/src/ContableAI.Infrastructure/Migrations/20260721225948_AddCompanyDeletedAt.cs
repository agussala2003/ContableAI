using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyDeletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Companies",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Companies");
        }
    }
}
