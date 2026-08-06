using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBankAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BankAccountId",
                table: "JournalEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BankAccountId",
                table: "BankTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Alias = table.Column<string>(type: "text", nullable: false),
                    AccountNumber = table.Column<string>(type: "text", nullable: true),
                    NormalizedNumber = table.Column<string>(type: "text", nullable: true),
                    Cbu = table.Column<string>(type: "text", nullable: true),
                    BankCode = table.Column<string>(type: "text", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ARS"),
                    ContraAccountName = table.Column<string>(type: "text", nullable: false),
                    ChartOfAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    StudioTenantId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankAccounts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_CompanyId_BankAccountId",
                table: "JournalEntries",
                columns: new[] { "CompanyId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_BankAccountId_Date",
                table: "BankTransactions",
                columns: new[] { "BankAccountId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_CompanyId_NormalizedNumber",
                table: "BankAccounts",
                columns: new[] { "CompanyId", "NormalizedNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_StudioTenantId",
                table: "BankAccounts",
                column: "StudioTenantId");

            BackfillFromLegacyCompanyFields(migrationBuilder);
        }

        /// <summary>
        /// Migra el modelo de "una cuenta en pesos + una en dólares por empresa" (los campos
        /// sueltos <c>Companies.BankAccountName</c> / <c>UsdBankAccountName</c>) a filas reales de
        /// <c>BankAccounts</c>, y vincula el historial existente.
        ///
        /// El vínculo se hace por (empresa, moneda), que es exactamente el criterio con el que hoy
        /// <c>GenerateJournalEntriesCommandHandler.TryResolveBankAccount</c> elige la contrapartida:
        /// el backfill reproduce el estado actual del sistema, no lo reinterpreta.
        ///
        /// Los movimientos sin empresa (bucket legacy) y los de empresas que nunca configuraron
        /// una cuenta quedan con <c>BankAccountId</c> nulo a propósito: hoy tampoco se pueden
        /// asentar, así que el backfill no les inventa una cuenta.
        /// </summary>
        private static void BackfillFromLegacyCompanyFields(MigrationBuilder migrationBuilder)
        {
            // NormalizedNumber queda NULL: estas cuentas todavía no tienen número conocido. En
            // Postgres los NULL son distintos entre sí dentro de un índice único, así que la cuenta
            // en pesos y la de dólares de una misma empresa conviven sin violar
            // IX_BankAccounts_CompanyId_NormalizedNumber.
            migrationBuilder.Sql("""
                INSERT INTO "BankAccounts" (
                    "Id", "CompanyId", "Alias", "AccountNumber", "NormalizedNumber", "Cbu",
                    "BankCode", "Currency", "ContraAccountName", "ChartOfAccountId", "IsActive",
                    "StudioTenantId")
                SELECT gen_random_uuid(), c."Id", TRIM(c."BankAccountName"), NULL, NULL, NULL,
                       NULL, 'ARS', TRIM(c."BankAccountName"), NULL, TRUE, c."StudioTenantId"
                FROM "Companies" c
                WHERE COALESCE(TRIM(c."BankAccountName"), '') <> '';
                """);

            migrationBuilder.Sql("""
                INSERT INTO "BankAccounts" (
                    "Id", "CompanyId", "Alias", "AccountNumber", "NormalizedNumber", "Cbu",
                    "BankCode", "Currency", "ContraAccountName", "ChartOfAccountId", "IsActive",
                    "StudioTenantId")
                SELECT gen_random_uuid(), c."Id", TRIM(c."UsdBankAccountName"), NULL, NULL, NULL,
                       NULL, 'USD', TRIM(c."UsdBankAccountName"), NULL, TRUE, c."StudioTenantId"
                FROM "Companies" c
                WHERE COALESCE(TRIM(c."UsdBankAccountName"), '') <> '';
                """);

            // Cada INSERT produce a lo sumo una cuenta por (empresa, moneda), así que los UPDATE
            // de abajo son deterministas: no hay dos candidatas para un mismo movimiento.
            migrationBuilder.Sql("""
                UPDATE "BankTransactions" t
                SET "BankAccountId" = ba."Id"
                FROM "BankAccounts" ba
                WHERE ba."CompanyId" = t."CompanyId"
                  AND ba."Currency"  = t."Currency"
                  AND t."BankAccountId" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "JournalEntries" j
                SET "BankAccountId" = ba."Id"
                FROM "BankAccounts" ba
                WHERE ba."CompanyId" = j."CompanyId"
                  AND ba."Currency"  = j."Currency"
                  AND j."BankAccountId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankAccounts");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_CompanyId_BankAccountId",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_BankAccountId_Date",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "BankAccountId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "BankAccountId",
                table: "BankTransactions");
        }
    }
}
