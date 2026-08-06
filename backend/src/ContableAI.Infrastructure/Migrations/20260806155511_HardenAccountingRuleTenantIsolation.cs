using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContableAI.Infrastructure.Migrations
{
    /// <summary>
    /// Hardening de aislamiento multi-tenant de <c>AccountingRules</c>.
    ///
    /// <c>StudioTenantId</c> pasa de <c>uuid</c> a <c>text</c> para poder compararse directamente
    /// contra el tenant del usuario (que es <c>text</c>, igual que <c>Companies.StudioTenantId</c>)
    /// en el Global Query Filter, sin casts no traducibles a SQL. Además se estampa el estudio en
    /// TODAS las reglas, no solo en las de estudio: es el ancla del filtro.
    /// </summary>
    public partial class HardenAccountingRuleTenantIsolation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Postgres no tiene assignment cast de uuid a text: el USING es obligatorio.
            // El índice IX_AccountingRules_StudioTenantId se reconstruye solo.
            migrationBuilder.Sql("""
                ALTER TABLE "AccountingRules"
                ALTER COLUMN "StudioTenantId" TYPE text USING "StudioTenantId"::text;
                """);

            // Backfill de las reglas de empresa, que nunca llevaron estudio estampado.
            // Es obligatorio: el filtro global es fail-closed, así que una regla de empresa sin
            // estudio quedaría invisible para su propio dueño (el estudio "perdería" sus reglas).
            migrationBuilder.Sql("""
                UPDATE "AccountingRules" r
                SET "StudioTenantId" = c."StudioTenantId"
                FROM "Companies" c
                WHERE r."CompanyId" = c."Id"
                  AND r."StudioTenantId" IS NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revertir la desnormalización: en el modelo anterior solo las reglas de estudio
            // (CompanyId null) llevaban tenant.
            migrationBuilder.Sql("""
                UPDATE "AccountingRules" SET "StudioTenantId" = NULL WHERE "CompanyId" IS NOT NULL;
                """);

            // Los identificadores de estudio que no son UUID (legacy, ej. 'ESTUDIO_DEFAULT') no
            // tienen representación en el tipo anterior: se descartan para que el cast no falle.
            migrationBuilder.Sql("""
                UPDATE "AccountingRules" SET "StudioTenantId" = NULL
                WHERE "StudioTenantId" IS NOT NULL
                  AND "StudioTenantId" !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "AccountingRules"
                ALTER COLUMN "StudioTenantId" TYPE uuid USING "StudioTenantId"::uuid;
                """);
        }
    }
}
