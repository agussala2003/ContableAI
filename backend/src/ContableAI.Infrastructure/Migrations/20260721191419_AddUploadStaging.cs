using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StagedUploadFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedUploadFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UploadJobResults",
                columns: table => new
                {
                    JobId = table.Column<string>(type: "text", nullable: false),
                    StudioTenantId = table.Column<string>(type: "text", nullable: false),
                    IsSuccess = table.Column<bool>(type: "boolean", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadJobResults", x => x.JobId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UploadJobResults_StudioTenantId",
                table: "UploadJobResults",
                column: "StudioTenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StagedUploadFiles");

            migrationBuilder.DropTable(
                name: "UploadJobResults");
        }
    }
}
