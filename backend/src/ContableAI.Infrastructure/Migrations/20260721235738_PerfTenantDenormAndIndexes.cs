using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerfTenantDenormAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StudioTenantId",
                table: "BankTransactions",
                type: "text",
                nullable: true);

            // P-2 · Backfill: copia el estudio desde Companies a las transacciones existentes.
            // Un solo UPDATE set-based; las transacciones sin empresa (CompanyId null) quedan
            // con StudioTenantId null → invisibles para usuarios con tenant, igual que antes.
            migrationBuilder.Sql("""
                UPDATE "BankTransactions" AS bt
                SET    "StudioTenantId" = c."StudioTenantId"
                FROM   "Companies" AS c
                WHERE  bt."CompanyId" = c."Id";
                """);

            // P-3 · Búsqueda de descripciones sin sequential scan:
            //   1. Extensiones: unaccent (ya usada en runtime por las queries, ahora declarada
            //      en migración para que un entorno limpio funcione) y pg_trgm (índices trigram).
            //   2. f_unaccent(): wrapper IMMUTABLE de unaccent() — Postgres exige inmutabilidad
            //      para indexar por expresión, y unaccent() no lo es (depende del search_path).
            //      El DbFunction "Unaccent" del DbContext mapea a esta misma función para que la
            //      expresión de la query coincida EXACTAMENTE con la del índice.
            //   3. Índice GIN trigram sobre f_unaccent("Description"): sirve ILIKE '%término%'
            //      (wildcard inicial incluido, donde un btree no ayuda).
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS unaccent;
                CREATE EXTENSION IF NOT EXISTS pg_trgm;

                CREATE OR REPLACE FUNCTION public.f_unaccent(text)
                RETURNS text
                LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
                AS $$ SELECT public.unaccent('public.unaccent', $1) $$;

                CREATE INDEX IF NOT EXISTS "IX_BankTransactions_Description_trgm"
                ON "BankTransactions"
                USING GIN (public.f_unaccent("Description") gin_trgm_ops);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_CompanyId_Amount",
                table: "BankTransactions",
                columns: new[] { "CompanyId", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_CompanyId_AssignedAccount",
                table: "BankTransactions",
                columns: new[] { "CompanyId", "AssignedAccount" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_CompanyId_SortOrder_Date",
                table: "BankTransactions",
                columns: new[] { "CompanyId", "SortOrder", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_StudioTenantId",
                table: "BankTransactions",
                column: "StudioTenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Se dropea índice y función propios; las extensiones se dejan instaladas (pueden
            // estar en uso por otros objetos y CREATE EXTENSION es idempotente).
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_BankTransactions_Description_trgm";
                DROP FUNCTION IF EXISTS public.f_unaccent(text);
                """);

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_CompanyId_Amount",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_CompanyId_AssignedAccount",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_CompanyId_SortOrder_Date",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_StudioTenantId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "StudioTenantId",
                table: "BankTransactions");
        }
    }
}
