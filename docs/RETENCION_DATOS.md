# Política de Retención y Tratamiento de Datos

> Vigente desde 2026-07-21. Complementa a `RUNBOOK_DB.md` (backups/PITR en Neon) y cierra los
> hallazgos P-2, P-4, P-5 y P-6 de `AUDITORIA.MD`. Marco normativo: Ley 25.326 de Protección
> de Datos Personales (Argentina), con criterios alineados a GDPR.

## Resumen por clase de dato

| Dato | Contenido sensible | Retención | Mecanismo de purga |
|---|---|---|---|
| `StagedUploadFiles` | Bytes completos del PDF del extracto | Minutos (flujo normal); **24 h** máximo | El handler borra la fila al consumirla; huérfanos: `DataRetentionJob` diario (P-5) |
| `UploadJobResults` | JSON con transacciones parseadas (importes, descripciones) | **30 días** | `DataRetentionJob` diario (P-4) |
| `Company` soft-deleted | CUIT, razón social + todo su historial | **90 días** desde `DeletedAt`, luego hard-delete en cascada | `DataRetentionJob` diario (P-2, purga diferida) |
| Datos de negocio del tenant (transacciones, asientos, reglas, vouchers) | Historial financiero completo | Mientras la cuenta esté activa | Cierre de cuenta (`DeleteStudioTenantCommand`, P-1) o purga de empresa vencida |
| `RefreshTokens` | Hash de sesión | Expiran a los 7 días; se eliminan con el usuario/tenant | `DeleteUserHandler` / cierre de cuenta |
| `AuditLogs` | Ver política dedicada abajo | **5 años**, seudonimizados tras el cierre de cuenta | Seudonimización inmediata al cierre; borrado definitivo manual/anual |

Las ventanas son configurables por entorno en la sección `DataRetention` de `appsettings.json`
(`UploadJobResultsDays`, `StagedFileOrphanHours`, `SoftDeletedCompanyDays`); los valores de la
tabla son la política oficial y los defaults del código
([DataRetentionOptions.cs](../backend/src/ContableAI.Infrastructure/Options/DataRetentionOptions.cs)).

## Política de `AuditLogs` (P-6)

**Qué contienen:** actor (`UserId`, `UserEmail`), acción, entidad afectada y diff JSON
(`Changes`) de cada escritura sobre transacciones, reglas y asientos. El diff puede incluir
importes, descripciones de movimientos y CUITs.

**Retención:** los registros de auditoría se conservan **5 años** desde su creación. La
retención larga es deliberada: en una plataforma financiera la trazabilidad de quién tocó qué
asiento es un requisito de defensa ante disputas e inspecciones, y prevalece sobre la
minimización mientras exista base legal (relación contractual vigente o plazo de prescripción).

**Tratamiento ante cierre de cuenta (derecho al olvido):** los `AuditLogs` **no se borran**
con el tenant — se **seudonimizan** de inmediato y de forma irreversible
(`StudioTenantPurger.AnonymizeAuditLogsAsync`, P-1):

- `UserEmail` → `deleted-user-{userId}@anonymized.local` (se pierde el vínculo con la persona;
  el `userId` ya no referencia a ningún usuario existente).
- `Changes` → `null` (el diff es la parte con datos financieros del titular).
- Sobrevive: acción, entidad, IDs técnicos y timestamp — suficiente para acreditar QUE hubo
  actividad auditada, sin poder reconstruir SU contenido.

Lo mismo aplica al borrado individual de un usuario (`DELETE /api/admin/users/{id}`): sus
filas de auditoría quedan seudonimizadas aunque el estudio siga operando.

**Registro del cierre:** cada ejecución de cierre de cuenta deja un `AuditLog` propio
(`EntityName = "StudioTenant"`) con el email del SystemAdmin ejecutante, el motivo declarado
y el conteo por tabla de lo eliminado. Ese registro es la evidencia del cumplimiento del
pedido y se conserva los mismos 5 años.

**Borrado definitivo:** cumplidos los 5 años, las filas son elegibles para borrado. La purga
no está automatizada a propósito (volumen bajo, decisión con revisión humana): ejecutarla
como tarea anual de mantenimiento, `DELETE FROM "AuditLogs" WHERE "Timestamp" < now() - interval '5 years'`.

## Interacción con backups (Neon)

La purga lógica no elimina los datos de los backups: una fila purgada persiste en la ventana
de PITR hasta que ésta expira (ver `RUNBOOK_DB.md`). La ventana de PITR forma parte del plazo
efectivo de retención y así debe informarse ante un pedido de acceso/supresión: el dato deja
de estar disponible operativamente al purgarse y desaparece por completo al vencer la ventana
de PITR.

## Operación

- Job: `DataRetentionJob` (`data-retention` en el dashboard de Hangfire, corrida diaria,
  lock distribuido — una sola ejecución aunque haya N réplicas).
- Borrados set-based (`ExecuteDeleteAsync`): un `DELETE` por tabla, sin materializar filas.
- Idempotente: reejecutarlo manualmente desde el dashboard es seguro.
- Los conteos purgados se loguean con nivel `Information` (`[RETENTION]`) y quedan
  correlacionados por el Correlation ID del job (O-1).
