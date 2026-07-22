# 📋 Reporte Final de Auditoría y Resoluciones Técnicas — ContableAI

> **Estado: AUDITORÍA CERRADA.** Todos los hallazgos de código de las seis áreas auditadas
> fueron resueltos y verificados en la rama `dev`.
> **Cierre:** 2026-07-21 · **Verificación final:** backend **211/211 tests en verde** ·
> frontend **21/21 tests en verde**.
>
> Este documento es el registro histórico definitivo de la auditoría: qué se auditó, qué se
> encontró y cómo se resolvió. Las únicas tareas que restan son **operativas** (Git, Render,
> Neon) y están consolidadas en la sección
> [Acciones Operativas Post-Deploy](#8-acciones-operativas-post-deploy).

---

## 1. Alcance de la auditoría

La auditoría se ejecutó área por área sobre el repositorio completo (backend .NET, frontend
Angular, infraestructura y documentación), cubriendo seis dimensiones:

1. **Seguridad de la Información** — autenticación/autorización, OWASP, cifrado en tránsito y
   en reposo, gestión de secretos.
2. **Calidad de Código y Funcionalidad** — cobertura de tests, deuda técnica, duplicación,
   integraciones externas.
3. **Rendimiento y Escalabilidad** — consumo de CPU/RAM del OCR, tiempos de respuesta,
   comportamiento bajo concurrencia.
4. **Arquitectura e Infraestructura (SRE)** — manejo global de errores, trazabilidad de logs,
   *cloud-readiness*, respaldo de base de datos.
5. **Usabilidad y Accesibilidad (UX/A11y)** — navegación, accesibilidad por teclado y lectores
   de pantalla, diseño responsivo.
6. **Cumplimiento Legal y Privacidad** — Ley 25.326 de Protección de Datos Personales
   (Argentina), licencias de dependencias de terceros.

---

## 2. Seguridad de la Información — Resoluciones Aplicadas

### Hallazgos críticos y altos

- **C-1 · Aislamiento multi-tenant.** Global Query Filters por tenant en EF Core.
- **C-2 · Secretos fuera de configuración.** Secretos removidos de `appsettings` y
  `docker-compose` parametrizado. *La purga del historial de Git y la rotación de credenciales
  quedan como acción operativa (§8).*
- **A-1 · Password de admin obligatoria en producción.** Sin `SEED_ADMIN_PASSWORD` (≥ 12
  caracteres) la API no arranca, por diseño.
- **A-2 · Dashboard de Hangfire protegido.** `/hangfire` exige rol `SystemAdmin`.
- **A-3 · Refresh tokens endurecidos.** Refresh token en cookie `HttpOnly`, access token de
  30 minutos y token en memoria en el frontend (nunca en `localStorage`).

### Hallazgos medios

- **M-1 ·** `UseForwardedHeaders` configurado para el proxy de Render.
- **M-2 ·** Validación de *magic bytes* en la subida de archivos.
- **M-3 ·** CUIT único por tenant (índice + validación).
- **M-4 ·** Políticas `RequireStudioOwner` en backend, con *gating* equivalente en la UI.

### Hallazgos bajos y observaciones de código

- **B-1a · `AllowedHosts` restringido.** El
  [appsettings.json](../backend/src/ContableAI.API/appsettings.json) base quedó fijado a
  `contableai-api.onrender.com;localhost;127.0.0.1`; Development lo abre a `*` en su override.
  Si se agrega un dominio custom, se sobreescribe con la env var `AllowedHosts` en Render sin
  tocar código.
- **B-1b · Enumeración de emails en registro.** `register` (invitados, flujo asíncrono sin
  login) es de **respuesta constante**: con email duplicado devuelve el mismo `202` y el mismo
  mensaje que el alta real — indistinguible para un atacante. `register-studio` conserva el
  `409` como trade-off aceptado y documentado en el código: es self-serve con login inmediato
  (devuelve JWT) y una respuesta genérica exigiría verificación por email que hoy no existe;
  el mensaje pasó a ser neutral. Mitigan: `forgot-password` y `register` constantes.
- **B-1c · Token de reset con CSPRNG y hash en BD.**
  [PasswordResetTokens.cs](../backend/src/ContableAI.Infrastructure/Services/PasswordResetTokens.cs)
  (mismo esquema que los refresh tokens de A-3): token de 256 bits de `RandomNumberGenerator`,
  persistencia solo del hash SHA-256 y comparación en tiempo constante (`FixedTimeEquals`).
  Corregido en los **dos** generadores: `ForgotPasswordHandler` y `SendAdminPasswordResetHandler`
  (este último no figuraba en la auditoría pero tenía el mismo `Guid.NewGuid()`). Los tokens
  legados en claro fallan la verificación y expiran solos (≤ 24 h). Tests en
  [PasswordResetSecurityTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/PasswordResetSecurityTests.cs).
- **Consistencia del tenant del admin.** `SeedExtensions` usa `TenantConstants.System`
  (`"SYSTEM"`), igual que el endpoint de seed-admin. Las bases ya sembradas conservan
  `"system"` en minúsculas — inocuo (el bypass de filtros es por rol, no por string) y no
  amerita migración de datos.

Además se completó el soporte **multi-moneda (ARS/USD)** detectado durante esta fase.

---

## 3. Calidad de Código y Funcionalidad — Resoluciones Aplicadas

- **CI real en GitHub Actions.** El workflow anterior solo compilaba y corría únicamente en
  `main`, por lo que los tests nunca se ejecutaban automáticamente. Ahora dispara en push/PR a
  `main` y `dev`, y ejecuta `dotnet test` + `ng test --no-watch`: la CI falla si algún test
  rompe o no compila.
- **Desmembramiento de `PdfBankParser`** (ex God Class de ~2.100 líneas) hacia Strategy +
  Template Method: `IBankStatementParser`, `TabularStatementParser` (motor tabular compartido
  con hooks virtuales) y una clase por banco (BBVA, Galicia, Credicoop, MercadoPago, Ciudad,
  Generic). El orquestador quedó en ~230 líneas. La lectura física se abstrajo detrás de
  `IStatementTextExtractor` (DIP), lo que permitió reactivar la interpretación de los cuatro
  bancos principales con **fixtures de texto ficticias** (sin PDFs reales ni datos sensibles)
  en lugar de los tests `Skipped` que dependían de archivos no versionados.
- **Refactor de los orquestadores gigantes** `UploadBankStatementHandler` y
  `GenerateJournalEntriesCommandHandler`: los métodos `Handle` (~280 y ~240 líneas) se
  descompusieron en pasos privados con nombre propio (validación de archivos, resolución de
  tenant, chequeo de cuota, presupuesto de deduplicación, carga de reglas, clasificación,
  proyección de líneas de asiento), cada uno cubierto por tests de integración con EF InMemory.
- **Unificación de la firma de deduplicación** en el Dominio (`TransactionSignatureBuilder`):
  antes se interpolaba por separado en dos puntos de `UploadBankStatementHandler`, con riesgo
  de divergencia silenciosa; ahora existe una única fuente de verdad reutilizada por ambos.
- **Resiliencia de red con Polly** (`Microsoft.Extensions.Resilience`) en `SmtpEmailService`:
  timeout absoluto (30 s) → retry con backoff exponencial + jitter (3 intentos) → timeout por
  intento (10 s), con la fábrica de pipelines (`ResiliencePipelines`) centralizada y lista para
  reutilizarse en integraciones HTTP futuras (pasarelas de pago, APIs de terceros).
- **Cobertura crítica de frontend** con `HttpTestingController` (primer uso del patrón en el
  proyecto): el interceptor de errores (`error.interceptor.ts` — flujo 401 → silent-refresh →
  retry, y logout en sus dos variantes de fallo) y `reconciliation.service.ts` (estado y
  filtros, pila de deshacer, polling del job de generación de asientos con `vi.useFakeTimers`).

---

## 4. Rendimiento y Escalabilidad — Resoluciones Aplicadas

### Hallazgos críticos

- **C-1 / C-2 · Upload asíncrono y protección de memoria.** El OCR y el parseo de extractos ya
  no corren de forma síncrona en el hilo del request HTTP (riesgo de *thread-starvation* de
  toda la API y de OOM bajo subidas concurrentes). El endpoint de subida valida rápido
  (tamaño/extensión/magic bytes) y encola el procesamiento como job de Hangfire reutilizando el
  patrón MediatR ya existente — `backgroundJobClient.Enqueue<ISender>(sender => sender.Send(command))`
  ([TransactionEndpoints.cs:82](../backend/src/ContableAI.API/Endpoints/TransactionEndpoints.cs#L82)).
  Se agregaron dos tablas en PostgreSQL: `StagedUploadFiles` (staging del archivo entre el
  encolado y la ejecución) y `UploadJobResults` (resultado durable, leído por el endpoint de
  polling). El handler ya no depende de `ICurrentTenantService` (no hay `HttpContext` dentro de
  un job): el tenant viaja explícito en el command
  ([UploadBankStatementHandler.cs:179](../backend/src/ContableAI.Infrastructure/Features/Transactions/UploadBankStatementHandler.cs#L179)).
- **Protección de CPU/RAM.** `OcrStatementExtractor` eliminó la copia redundante a
  `MemoryStream`/`byte[]` y trabaja directo sobre el `Stream` recibido (PdfPig y PDFtoImage lo
  soportan nativo); el OCR concurrente queda acotado con `SemaphoreSlim(2, 2)`
  ([OcrStatementExtractor.cs:33](../backend/src/ContableAI.Infrastructure/Services/OcrStatementExtractor.cs#L33)).
- **C-3 · Optimización de lecturas.** `GetTransactions` ya no materializa toda la tabla
  filtrada del estudio para poblar los dropdowns de filtro; son 3 consultas `.Distinct()`
  independientes resueltas en PostgreSQL
  ([TransactionEndpoints.cs:251](../backend/src/ContableAI.API/Endpoints/TransactionEndpoints.cs#L251)),
  apoyadas en el índice `IX_BankTransactions_CompanyId_Date` — escala con la cantidad de
  valores distintos, no con el volumen histórico.
- **Polling en el frontend.** `reconciliation.service.ts` pollea el resultado del job (mismo
  patrón que la generación de asientos) reproduciendo exactamente la UX anterior: toasts,
  modal de duplicados, ajuste de filtros por período importado y confirm-dialog de reglas —
  el cambio de arquitectura es transparente para el usuario.

### Optimizaciones P-1 a P-11

- **P-1 · Pooling real de DbContext.** `AddPooledDbContextFactory` + registración scoped que
  toma la instancia del pool y le estampa el tenant con `ContableAIDbContext.SetTenant()` (la
  captura de `ICurrentTenantService` salió del constructor, incompatible con pooling).
  `SetTenant` se llama en **cada** lease, por lo que una instancia reciclada nunca conserva el
  tenant del request anterior — cubierto por test explícito de reuso en
  [TenantIsolationTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/TenantIsolationTests.cs).
- **P-2 · `StudioTenantId` desnormalizado en `BankTransaction`.** Migración
  `PerfTenantDenormAndIndexes` con backfill set-based e índice propio. El Global Query Filter
  ancla por la columna directa — se eliminó el `JOIN` a `Company` que cargaba cada query de
  transacciones. Bonus: el cierre de cuenta (P-1 de privacidad) ahora barre también las
  transacciones huérfanas (`CompanyId` null por FK SetNull) antes inalcanzables.
- **P-3 · Índice trigram para la búsqueda.** Migración que habilita `unaccent` + `pg_trgm`,
  crea el wrapper IMMUTABLE `f_unaccent()` y el índice
  `GIN (f_unaccent("Description") gin_trgm_ops)`. El DbFunction `Unaccent` mapea a `f_unaccent`
  para que la expresión de la query coincida con la del índice — el `ILIKE '%término%'` deja
  de ser sequential scan.
- **P-4 · Índices de respaldo para el sort de la grilla.** `(CompanyId, SortOrder, Date)` para
  el orden por defecto, más `(CompanyId, Amount)` y `(CompanyId, AssignedAccount)` para los
  sorts por columna.
- **P-6 · `CheckManualSuggestionAsync` batcheado.** `CheckManualSuggestionsBatchAsync` resuelve
  N grupos con 3 consultas fijas y un único `SaveChanges` (antes ~3 round-trips por grupo), con
  la misma semántica (umbral ≥ 3, reactivación de Rejected, keywords solo en
  creación/reactivación). El update individual usa el mismo camino con lote de 1.
- **P-7 · `ProactiveLearningJob` acotado por empresa.** Procesa empresa por empresa: `DISTINCT`
  de empresas con actividad manual en la ventana y luego una pasada por empresa con chequeos
  batcheados. El pico de memoria queda acotado al volumen de una empresa, no a los 2 años de
  movimientos de toda la plataforma. Tests en
  [ProactiveLearningJobTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/ProactiveLearningJobTests.cs).
- **P-8 · `AfipCombinationService` — evaluado; se mantiene síncrono con presupuesto duro.** El
  backtracking ya está podado y acotado (≤ 5 VEPs por combo, ≤ 5 alternativas, ventana ±2 días)
  y en datos reales termina en milisegundos. Se agregó un presupuesto de cómputo de 2 s por
  request: si un caso patológico lo agota, el endpoint devuelve sugerencias parciales y loguea
  un warning — ese log recurrente es el trigger explícito para pasarlo a background.
- **P-9 · Workers y colas de Hangfire explícitos.** Dos servers en el mismo proceso: cola
  `uploads` (2 workers, alineados con el `SemaphoreSlim(2,2)` del OCR) y cola `default`
  (3 workers para asientos, cruce AFIP, retención y aprendizaje). `HangfireQueueRoutingFilter`
  enruta `UploadBankStatementCommand` a `uploads` por tipo de command (también en reintentos).
  Antes: default de hasta 20 workers sin control compitiendo con la API.
- **P-10 · Borrado masivo set-based.** `DeleteAllTransactions` reemplazó `RemoveRange` +
  `SaveChangesAsync` por `ExecuteDeleteAsync`
  ([TransactionEndpoints.cs:531](../backend/src/ContableAI.API/Endpoints/TransactionEndpoints.cs#L531)).
  Como eso elude `AuditInterceptor`, se compensa con una única fila manual en `AuditLogs`
  (`Deleted` / `BankTransaction` / `EntityId="BULK"` + cantidad borrada).
- **P-11 · Performance de UI.** `TransactionGrid` pasa a `ChangeDetectionStrategy.OnPush`
  ([transaction-grid.ts:18](../frontend/src/app/features/reconciliation/components/transaction-grid/transaction-grid.ts#L18));
  el componente ya era 100 % signals, que integran nativamente con OnPush.

*Nota: P-5 no existió como hallazgo independiente en el informe de rendimiento.*

---

## 5. Arquitectura e Infraestructura (SRE) — Resoluciones Aplicadas

Bloque de *Infrastructure & Observability Hardening*: cierra los hallazgos de la auditoría SRE
(riesgos de infraestructura, brechas de observabilidad y resiliencia). Sin cambios funcionales
de negocio; foco en *cloud-readiness* para contenedores efímeros (Render) y trazabilidad de
punta a punta.

- **R-1 · Resiliencia de conexión a PostgreSQL.** `UseNpgsql` con
  `EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: 10s)` + `CommandTimeout(30)`
  ([ServiceExtensions.cs](../backend/src/ContableAI.API/Extensions/ServiceExtensions.cs)).
  Los fallos transitorios del PostgreSQL gestionado se reintentan con backoff en vez de
  propagarse al usuario. No hizo falta `CreateExecutionStrategy`: no existe uso de
  `BeginTransaction()` en el código.
- **R-2 / R-3 · Health checks reales con separación liveness/readiness.** Antes
  `AddHealthChecks()` no registraba ningún check y `/healthz` devolvía siempre `Healthy` aunque
  la base estuviera caída. Ahora `AddNpgSql` (tag `ready`) + dos endpoints: `/health/live`
  (liveness sin dependencias externas — un micro-corte de la base no reinicia el contenedor) y
  `/health/ready` (readiness — PostgreSQL alcanzable)
  ([Program.cs](../backend/src/ContableAI.API/Program.cs)).
- **R-5 · `ProactiveLearning` como cron de Hangfire.** Eliminado el `BackgroundService` con
  `Task.Delay(24h)` (corría en cada réplica — trabajo duplicado y *race* al insertar
  sugerencias; su timer se reiniciaba en cada redeploy). Reemplazado por
  [ProactiveLearningJob](../backend/src/ContableAI.Infrastructure/BackgroundJobs/ProactiveLearningJob.cs)
  agendado con `RecurringJob.AddOrUpdate(Cron.Daily)`: corrida única garantizada por el lock
  distribuido de Hangfire sobre PostgreSQL, scheduling durable. El acotado por empresa se
  completó en P-7 (§4).
- **O-1 · Correlation ID de punta a punta.**
  [CorrelationIdMiddleware](../backend/src/ContableAI.API/Middleware/CorrelationIdMiddleware.cs)
  (lee/genera y **sanitiza** el header `X-Correlation-Id`, lo publica en `LogContext`,
  `Activity`, `HttpContext.Items`, header de respuesta y un contexto ambiente `AsyncLocal`) +
  [HangfireCorrelationIdFilter](../backend/src/ContableAI.API/Common/HangfireCorrelationIdFilter.cs)
  (el filtro de cliente captura el ID al encolar y el de servidor lo restaura en el worker).
  Cierra la traza Kestrel → MediatR → Hangfire.
- **O-2 · Logs aptos para contenedores efímeros.** Eliminado el *Serilog File Sink* (se perdía
  en cada redeploy y no se agregaba entre réplicas). Queda solo el Console sink (stdout
  estructurado), que la plataforma captura. Removido el paquete `Serilog.Sinks.File`.
- **O-3 · `traceId` en `ProblemDetails`.** El `GlobalExceptionHandler` inyecta el Correlation
  ID como `Extensions["traceId"]`, correlacionando el error que ve el usuario con los logs del
  servidor. El frontend lo muestra en el toast de error 500 para reporte a soporte.
- **B-1 · Estrategia de backup/retención documentada.** [RUNBOOK_DB.md](RUNBOOK_DB.md) corrige
  la asunción original ("Postgres gestionado en Render") — el motor real es **Neon**, hoy en
  plan Free. Define la política de retención (PITR nativo de Neon + `pg_dump` semanal como
  defensa en profundidad), RPO/RTO objetivo para la fase Beta (≤ 24 h / ≤ 4 h) y el
  procedimiento de restauración ante desastre. Los simulacros y verificaciones en el entorno
  productivo de Neon quedan como tareas operativas en el §6 del propio runbook.
- **B-2 · Cloud readiness.** Eliminados los endpoints `suggestions-debug` de
  `RulesEndpoints.cs` que leían/escribían `logs/suggestions-debug.jsonl` en disco local
  (código muerto que acoplaba falsamente al file system efímero).

---

## 6. Usabilidad y Accesibilidad (UX/A11y) — Resoluciones Aplicadas

Los fixes críticos de la conciliación se aplicaron en el commit `1e8cc4e`; el resto de los
hallazgos se cerró en una segunda pasada. **No quedan pendientes de UX/A11y.**

### Flujo de conciliación

- **UX-1 · Overlay bloqueante eliminado.** El polling de estado de los jobs (subida de
  extractos y generación de asientos) usa el contexto `SKIP_LOADING`: las requests periódicas
  ya no disparan el overlay global de carga y la UI queda usable mientras el job corre
  ([transaction.ts](../frontend/src/app/core/services/transaction.ts),
  [journal-entry.service.ts](../frontend/src/app/core/services/journal-entry.service.ts)).
- **UX-2 / UX-3 · Estado `_isGenerating` corregido.** El flag se mantiene activo durante todo
  el ciclo del job (no solo la request inicial), lo que deshabilita el botón de generación con
  un job en vuelo y evita el **doble asentamiento** por doble clic
  ([reconciliation.service.ts](../frontend/src/app/features/reconciliation/reconciliation.service.ts)).
  El spec de polling se actualizó para reflejar la nueva semántica.
- **FL-1 · Dropzone acumulativa.** La zona de subida de extractos acumula los archivos de
  selecciones sucesivas en vez de reemplazar la lista
  ([upload-zone.ts](../frontend/src/app/features/reconciliation/components/upload-zone/upload-zone.ts)).
- **Timeout del polling.** Ambos pollings de jobs tienen un tope de **5 minutos**
  (`JOB_POLL_TIMEOUT_MS`, implementado con `take(maxPolls)` sobre el `timer` para no interferir
  con `takeUntilDestroyed`): al agotarse se apaga el spinner y se muestra un aviso accionable.
  Cubierto por test con fake timers.
- **Dropzone de AFIP sin fricción.**
  [afip-zone.ts](../frontend/src/app/features/reconciliation/components/afip-zone/afip-zone.ts)
  acumula archivos de selecciones/arrastres sucesivos (misma ergonomía que FL-1), resetea el
  `<input>` para permitir re-elegir el mismo archivo, filtra no-PDF con aviso y valida nombres
  duplicados con toast. Operable por teclado (`role="button"`, `tabindex="0"`, Enter/Espacio).
- **Mensajes de error orientados al contador.**
  [error.interceptor.ts](../frontend/src/app/core/interceptors/error.interceptor.ts): red caída
  → mensaje de conexión claro; los errores de validación 400 traducen los campos PascalCase del
  backend a etiquetas en castellano (`FIELD_LABELS` + separador de PascalCase como fallback);
  404 y 500+ reformulados en voseo accionable, y el `traceId` de O-3 se presenta como "Código
  para soporte".

### Accesibilidad

- **A11y-1 · Modales accesibles.** Directiva
  [modal-a11y.directive.ts](../frontend/src/app/shared/directives/modal-a11y.directive.ts)
  (focus trap con Tab/Shift+Tab, foco inicial al abrir, cierre con Escape, restauración del
  foco al cerrar) aplicada a **todos** los modales. Todos llevan `role="dialog"`,
  `aria-modal="true"` y `aria-labelledby` hacia su título.
- **A11y-2 · Grilla operable por teclado.** En
  [transaction-grid.html](../frontend/src/app/features/reconciliation/components/transaction-grid/transaction-grid.html):
  la celda de cuenta asignada (antes solo doble clic) es focuseable y se edita con
  Enter/Espacio; el semáforo de confianza expone su tooltip como `aria-label`; el tooltip del
  ID externo se muestra también con foco; checkboxes de fila y cabecera con `aria-label`;
  columnas ordenables con `aria-sort`.
- **A11y-3 · Dropzones y botones de ícono.** Ambas dropzones son operables por teclado
  (Enter/Espacio abren el selector, anillo `focus-visible`). Todos los botones puramente ícono
  tienen `aria-label`; la paginación marca `aria-current="page"`; los toggles usan
  `aria-expanded`/`aria-pressed` según corresponda.
- **A11y-4 · Combobox y formularios.**
  [account-combobox](../frontend/src/app/shared/components/account-combobox/account-combobox.html)
  con semántica ARIA completa (`role="combobox"`, `aria-expanded`, `aria-autocomplete="list"`,
  listbox con `aria-selected`); login y modales de reglas con `<label for>`/`id` vinculados;
  todos los filtros de la grilla con `aria-label`.
- **A11y-5 · Anuncio de cambios de estado.** Los toasts se renderizan con `role="alert"` +
  `aria-live="polite"` (todos los resultados de polling/jobs terminan en un toast) y la página
  de conciliación marca `aria-busy` durante la carga.
- **A11y-6 · Foco visible.** `focus-visible:ring` en los nuevos controles interactivos; los
  controles nativos existentes conservan los estilos `focus:` de Tailwind.

---

## 7. Cumplimiento Legal y Privacidad — Resoluciones Aplicadas

Veredicto de licencias: **no hay GPL/AGPL en ninguna dependencia directa** (backend ni
frontend).

### Privacidad de datos (P-1 a P-6)

- **P-1 · Cierre formal de cuenta.** Comando `DeleteStudioTenantCommand` + endpoint
  `DELETE /api/admin/tenants/{studioTenantId}` (solo `SystemAdmin`, con `?reason=` opcional).
  Ejecuta dentro de una transacción (execution strategy compatible con el retry de R-1) la
  cascada completa del tenant vía
  [StudioTenantPurger.cs](../backend/src/ContableAI.Infrastructure/Features/Admin/StudioTenantPurger.cs):
  usuarios, `RefreshTokens`, empresas (incluidas soft-deleted), transacciones, asientos y
  líneas, vouchers AFIP, reglas de empresa y de estudio, sugerencias, plan de cuentas propio,
  períodos cerrados, `UploadJobResults` y purga defensiva de `StagedUploadFiles` huérfanos.
  Los `AuditLogs` no se borran: se **seudonimizan** (email →
  `deleted-user-{userId}@anonymized.local`, diffs `Changes` vaciados), y la ejecución queda
  registrada con un AuditLog de cierre (solicitante, motivo y conteo por tabla, devuelto
  también por la API como evidencia). Deletes set-based (`ExecuteDeleteAsync`/
  `ExecuteUpdateAsync`). Tests de integración en
  [DeleteStudioTenantHandlerTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/DeleteStudioTenantHandlerTests.cs).
- **P-2 · Soft-delete de `Company` corregido.** `Company.DeletedAt` (migración
  `AddCompanyDeletedAt`) se estampa en el DELETE de forma idempotente, y `GetCompanyHandler`
  ya no devuelve por ID empresas dadas de baja (404, igual que una inexistente). La purga
  diferida (> 90 días) la ejecuta el job de retención de P-4. Tests en
  [CompanySoftDeleteTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/CompanySoftDeleteTests.cs).
- **P-3 · Residuos del hard-delete admin eliminados.** `DeleteUserHandler` delega en el mismo
  `StudioTenantPurger` cuando cae el último usuario del estudio (cascada idéntica al cierre
  formal); cuando quedan otros usuarios, borra los `RefreshTokens` del usuario y seudonimiza
  su email en `AuditLogs`.
- **P-4 · Retención de `UploadJobResults`.**
  [DataRetentionJob](../backend/src/ContableAI.Infrastructure/BackgroundJobs/DataRetentionJob.cs)
  (recurring job diario de Hangfire, mismo patrón que R-5: lock distribuido, corrida única
  entre réplicas) purga con `ExecuteDeleteAsync` los resultados con más de **30 días**
  (configurable vía
  [DataRetentionOptions.cs](../backend/src/ContableAI.Infrastructure/Options/DataRetentionOptions.cs)).
  El mismo job ejecuta la purga diferida de P-2: empresas con `DeletedAt` > 90 días se
  hard-deletean en cascada completa (`StudioTenantPurger.PurgeCompaniesAsync`). Tests en
  [DataRetentionJobTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/DataRetentionJobTests.cs).
- **P-5 · Red de seguridad de `StagedUploadFiles`.** El `DataRetentionJob` purga a diario los
  archivos de staging con más de **24 h** (configurable), cubriendo jobs de Hangfire que nunca
  ejecutaron (reintentos agotados, deploy a mitad de cola). Se suma a la purga defensiva del
  cierre de cuenta (P-1).
- **P-6 · Política de retención de `AuditLogs` documentada.** Formalizada en
  [RETENCION_DATOS.md](RETENCION_DATOS.md): retención de **5 años** por trazabilidad
  financiera, seudonimización inmediata e irreversible ante cierre de cuenta o borrado de
  usuario, registro del cierre como evidencia, borrado definitivo como tarea anual con revisión
  humana, e interacción con la ventana de PITR de Neon. Incluye la tabla completa de retención
  por clase de dato.

### Licenciamiento (L-1 y L-4)

- **L-1 · Migración al paquete oficial `PdfPig 0.1.15`.** Se eliminó
  `UglyToad.PdfPig 1.7.0-custom-5` de
  [ContableAI.Infrastructure.csproj](../backend/src/ContableAI.Infrastructure/ContableAI.Infrastructure.csproj)
  y se lo reemplazó por el paquete oficial del proyecto (ID `PdfPig`, Apache 2.0, ~26,5 M
  descargas). Conserva el namespace `UglyToad.PdfPig`, por lo que no hubo cambios de código.
  Suite completa validada contra el dataset real de PDFs.
  **Hallazgo adicional (más grave que lo auditado):** el ID `UglyToad.PdfPig` en nuget.org NO
  es del proyecto oficial — pertenece a un tercero no verificado (`grinay`), sin licencia, sin
  autor real ni URL de repo, con solo dos versiones prerelease. Cualquier restore limpio
  descargaba ese paquete de procedencia opaca. Recomendado: purgar
  `~/.nuget/packages/uglytoad.pdfpig*` en las máquinas del equipo y considerar bloquear el
  prefijo `UglyToad.*` vía `packageSourceMapping` en `nuget.config`.
- **L-4 · Gobernanza de MediatR.** MediatR 12.4.1 (Apache 2.0) quedó fijado con rango cerrado
  `[12.4.1, 13.0.0)` en
  [ContableAI.Application.csproj](../backend/src/ContableAI.Application/ContableAI.Application.csproj),
  con comentario explicativo: a partir de v13 MediatR pasó a licenciamiento comercial, y el
  rango impide que un upgrade automático cruce el major sin revisión de licencia previa.
  **Política adoptada:** no subir de major en dependencias sin revisión de licencia previa.

---

## 8. Acciones Operativas Post-Deploy

Tareas manuales fuera del código, a ejecutar sobre los entornos de Git, Render y Neon. Son las
únicas actividades abiertas al cierre de esta auditoría.

### Repositorio Git (críticas)

- **🔴 Purga del historial de Git + rotación de secretos (C-2).** Los secretos siguen siendo
  recuperables del historial (`git log -S` los encuentra en los commits `80d6c94`, `b54b4ee` y
  `14d1acf`); quitarlos de commits posteriores no los borra. Procedimiento:
  1. Rotar como quemados: nueva password de Neon, nueva `Jwt__Key`
     (`openssl rand -base64 48`), nueva `RESEND_API_KEY`.
  2. Purgar con `git filter-repo --replace-text` o BFG → `git push --force` → re-clone del
     equipo.
- **🔴 Purga de PDFs reales del historial de Git.** El commit `ee19a5e` hizo
  `git rm --cached` de los extractos bancarios y comprobantes AFIP con CUITs, importes y
  nombres de cuenta reales — ya no están en el árbol de trabajo ni se suben a futuro
  (`.gitignore` cubre `/tests/`), pero siguen recuperables del historial. Purgar con
  `git filter-repo` o BFG → `git push --force` → re-clone. Puede resolverse en la misma pasada
  que la purga de secretos, ya que ambas reescriben el historial.

### Render (antes del próximo deploy)

- **🔴 CRÍTICO — Health Check Path:** en Render → Settings, cambiar de `/healthz` a
  **`/health/ready`**. Si no se actualiza, Render seguirá consultando la ruta vieja (ahora 404)
  y marcará el servicio como no saludable, bloqueando el deploy.
- **⚙️ Variables de entorno:** `SEED_ADMIN_PASSWORD` (≥ 12 caracteres; sin ella la API no
  arranca, por diseño), `ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Smtp__Password`,
  `Frontend__BaseUrl`, `ASPNETCORE_ENVIRONMENT=Production`.
- **🍪 CORS/cookie:** verificar que `Frontend__BaseUrl` coincida **exactamente** con el origen
  del frontend (CORS con credenciales exige origen explícito).
- **🗄️ Migraciones:** `CuitUniquePerTenant`, `AddRefreshTokens`, `PerfTenantDenormAndIndexes`
  y `AddCompanyDeletedAt` se aplican solas al arrancar (`MigrateAsync`), o manualmente con
  `dotnet ef database update`.

### Validaciones post-deploy

- Verificar `/health/live` y `/health/ready` tras el primer deploy con estos cambios.
- **Cookie de refresh cross-site (A-3):** validar en staging que login → reload → sigue
  logueado. La cookie es `SameSite=None` (Vercel ↔ Render son cross-site) y navegadores que
  bloquean cookies de terceros podrían desloguear al recargar. No es resoluble desde código.
- Ejecutar las tareas operativas de backups del [RUNBOOK_DB.md](RUNBOOK_DB.md) §6 (verificar la
  ventana de retención vigente de Neon, simulacro de restauración).

---

## Anexo · Hoja de ruta Go-to-Market (referencia)

Secuencia acordada para el lanzamiento comercial, posterior al cierre de esta auditoría:

1. **Entorno de producción.** Migrar base de datos, backend y build optimizado del frontend a
   los servidores productivos definitivos.
2. **Smoke testing final.** Pruebas reales en producción: usuarios de prueba, subida de
   extractos y generación de asientos.
3. **Modelo de negocio.** Integrar la pasarela de pagos (Mercado Pago o Stripe) y el flujo de
   suscripciones.
4. **Legales y landing page.** Términos y Condiciones, Política de Privacidad y página
   comercial.
5. **Onboarding "Family & Friends".** Primeros beta testers reales de estudios contables.
6. **Marketing y ventas.** Redes sociales, contacto con estudios contables y demostraciones.
