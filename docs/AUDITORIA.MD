¡Buenas! Qué excelente noticia que ya estén pensando en sacar la aplicación a la cancha. Te aclaro que soy Gemini, la IA de Google, pero me parece perfecto armarte toda la estrategia acá para que después le bajes las instrucciones precisas a Claude Code con su modelo Fable.

Lanzar una plataforma SaaS financiera que procesa extractos bancarios en PDF requiere que la infraestructura esté sólida. Vamos a dividir esto en dos partes: la auditoría técnica y el paso a paso del lanzamiento.

### Auditoría Integral del Sistema

Para que Claude Code audite el repositorio de manera efectiva, te sugiero que lo guíes área por área (no le tires todo el código junto). Aquí tienes el enfoque:

* **Seguridad Informática:** Pídele que revise la autenticación y la autorización en los endpoints de .NET. Es crítico que escanee vulnerabilidades OWASP y garantice el cifrado de la información de los clientes, tanto en tránsito (TLS) como en reposo.


* **Calidad del Código y Funcionalidad:** Haz que evalúe la cobertura de pruebas unitarias y de integración. Claude debe buscar deuda técnica, código duplicado en los servicios y verificar la correcta conexión de las APIs externas.


* **Rendimiento y Escalabilidad:** El procesamiento del OCR puede consumir bastantes recursos. Pídele que analice el uso de memoria RAM y CPU. Debe evaluar los tiempos de respuesta y cómo se comporta el backend ante un pico de usuarios concurrentes.


* **Arquitectura e Infraestructura:** Que audite el manejo de errores globales y la trazabilidad de los logs en C# para una rápida resolución de fallos. También debe evaluar la escalabilidad en la nube y las estrategias de respaldo de la base de datos.


* **Usabilidad (UX/UI):** Dile que revise los componentes en Angular para garantizar que la navegación sea intuitiva. También puede verificar la accesibilidad y el diseño responsivo para distintas pantallas.


* **Cumplimiento Legal (Compliance):** Aunque Claude audite código, asegúrate de cumplir con la Ley 25.326 de Protección de Datos Personales en Argentina. Además, pídele que verifique las licencias de código abierto de terceros para evitar problemas de copyright o licenciamiento.



---

### Paso a Paso para Salir a la Venta

Una vez superada la auditoría, el *Go-to-Market* debería seguir este orden:

* **Paso 1: Entorno de Producción.** Migrar la base de datos PostgreSQL, el backend y el build optimizado del frontend a los servidores productivos definitivos.
* **Paso 2: Smoke Testing Final.** Realizar pruebas reales en producción creando usuarios de prueba, subiendo extractos y generando los asientos contables para confirmar que no hay caídas en el servidor.
* **Paso 3: Definición del Modelo de Negocio.** Integrar la pasarela de pagos (por ejemplo, Mercado Pago o Stripe) y conectar el flujo de suscripciones que charlaron.
* **Paso 4: Legales y Landing Page.** Redactar los Términos y Condiciones, Políticas de Privacidad y pulir la página de aterrizaje comercial para que los usuarios sin el sistema puedan conocer la herramienta.
* **Paso 5: Onboarding "Family & Friends".** Darle acceso a ese familiar tuyo que trabaja en estudios contables y a los contactos interesados de la facultad para que sean los primeros *beta testers* reales. Su feedback inicial vale oro.
* **Paso 6: Marketing y Ventas.** Empezar a darle movimiento a la cuenta de Instagram, contactar estudios contables por LinkedIn y hacer demostraciones reales del ahorro de tiempo que genera la plataforma.

¿Te gustaría que redactemos el primer prompt exacto para pasarle a Claude Code y arrancar con la auditoría de Seguridad Informática?

---

# 🔒 Auditoría de Seguridad de la Información — Pendientes

> Anotado el 2026-07-20. Estado del grueso del plan: **implementado y con tests verdes**
> (173/173 backend). Acá quedan registrados **solo los pendientes** para resolver más adelante.

## Ya completado (referencia rápida)
Críticas **C-1** (Global Query Filters por tenant) y **C-2** (secretos fuera de config + docker
parametrizado). Altas **A-1** (password admin obligatoria en prod), **A-2** (`/hangfire` con rol
SystemAdmin), **A-3** (refresh tokens en cookie HttpOnly + access token 30 min + token en memoria
en el front). Medias **M-1** (`UseForwardedHeaders`), **M-2** (magic bytes en subida), **M-3**
(CUIT único por tenant), **M-4** (políticas `RequireStudioOwner` en backend + gating de UI).
Multi-moneda (ARS/USD) completo. **No hay que rehacer nada de esto.**

## ⏳ Pendiente en código — Vulnerabilidades Bajas (B-1)

- [ ] **B-1a · `AllowedHosts` restringido.**
  Hoy `"AllowedHosts": "*"` en [appsettings.json](../backend/src/ContableAI.API/appsettings.json#L8).
  → Fijarlo al host de producción (o sobreescribir con env var en Render).

- [ ] **B-1b · Enumeración de emails en registro.**
  `register` y `register-studio` responden `409 "Ya existe un usuario con ese email."`
  ([AuthHandlers.cs:87 y :135](../backend/src/ContableAI.Infrastructure/Features/Auth/AuthHandlers.cs#L87)),
  a diferencia de `forgot-password` que sí es constante.
  → Unificar a una respuesta genérica, o aceptar el trade-off UX conscientemente.

- [ ] **B-1c · Token de reset con CSPRNG.**
  Se genera con `Guid.NewGuid().ToString("N")`
  ([AuthHandlers.cs:185](../backend/src/ContableAI.Infrastructure/Features/Auth/AuthHandlers.cs#L185)),
  que no garantiza aleatoriedad criptográfica.
  → Usar `RandomNumberGenerator` (≥128 bits) y **guardar el hash** del token, no el valor en claro.
  La expiración de 1 h ya está bien.

## ⏳ Observaciones menores (no bloqueantes, para el radar)

- [ ] **Cookie de refresh cross-site (A-3).** Es `SameSite=None` (Vercel ↔ Render son cross-site);
  navegadores que bloquean cookies de terceros podrían desloguear al recargar.
  → Validar en staging: login → reload → sigue logueado.
- [ ] **Inconsistencia del tenant del admin.** `SeedExtensions` usa `StudioTenantId = "system"` y
  `TenantConstants.System` usa `"SYSTEM"`. No afecta la seguridad (el bypass de filtros se basa en
  el **rol**, no en el string), pero conviene unificar.

## 🔧 Acciones operativas manuales (fuera de código — AÚN SIN HACER)

- [ ] **🔴 Purga del historial de Git + rotación de secretos (C-2).**
  Los secretos **siguen siendo recuperables del historial** (`git log -S 'npg_T4uWy2QlEwAq'` y
  `-S 'kSV1...'` los encuentran en commits `80d6c94`, `b54b4ee`, `14d1acf`). Quitarlos de commits
  posteriores no los borra.
  1. Rotar como quemados: nueva password de Neon, nueva `Jwt__Key` (`openssl rand -base64 48`),
     nueva `RESEND_API_KEY`.
  2. Purgar con `git filter-repo --replace-text` o BFG → `git push --force` → re-clone del equipo.
- [ ] **🔴 Purga de PDFs reales del historial de Git.**
  El commit `ee19a5e` (rama `dev`) hizo `git rm --cached` de los extractos bancarios y
  comprobantes AFIP con CUITs, importes y nombres de cuenta reales — ya no están en el árbol
  de trabajo ni se suben a futuro (`.gitignore` cubre `/tests/`), pero **siguen recuperables
  del historial de commits anteriores**, igual que los secretos de C-2. Purgar definitivamente
  con `git filter-repo` (o BFG Repo-Cleaner) → `git push --force` → re-clone del equipo. Puede
  resolverse en la misma pasada que la purga de secretos, ya que ambas reescriben el historial.
- [ ] **⚙️ Variables de entorno en Render** (antes del próximo deploy):
  `SEED_ADMIN_PASSWORD` (≥12, sin ella la API no arranca por diseño), `ConnectionStrings__DefaultConnection`,
  `Jwt__Key`, `Smtp__Password`, `Frontend__BaseUrl`, `ASPNETCORE_ENVIRONMENT=Production`.
- [ ] **🍪 Verificar CORS/cookie**: que `Frontend__BaseUrl` coincida **exactamente** con el origen del
  front (CORS con credenciales exige origen explícito).
- [ ] **🗄️ Migraciones**: `CuitUniquePerTenant` y `AddRefreshTokens` se aplican solas al arrancar
  (`MigrateAsync`), o manual con `dotnet ef database update`.

---

# 🏗️ Auditoría de Calidad de Código y Funcionalidad — Estado

> Anotado el 2026-07-21. Fase ejecutada en rama `dev` sobre el informe de auditoría de
> arquitectura y testing (plan de acción P0 → P2). Suite backend: **127 Passed / 65 Skipped /
> 0 Failed**. Suite frontend: **20 Passed / 0 Failed**.

## Ya completado (referencia rápida)

- **CI real en GitHub Actions.** El workflow antes solo compilaba (`dotnet build` / `npm run
  build`) y corría únicamente en `main`, así que los tests nunca se ejecutaban
  automáticamente. Ahora dispara en push/PR a `main` **y `dev`**, y agrega `dotnet test` +
  `ng test --no-watch`: la CI falla si algún test rompe o no compila.
- **Desmembramiento de `PdfBankParser`** (ex God Class de ~2.100 líneas) hacia Strategy +
  Template Method: `IBankStatementParser`, `TabularStatementParser` (motor tabular compartido
  con hooks virtuales) y una clase por banco (BBVA, Galicia, Credicoop, MercadoPago, Ciudad,
  Generic). El orquestador quedó en ~230 líneas. La lectura física se abstrajo detrás de
  `IStatementTextExtractor` (DIP), lo que permitió reactivar la interpretación de los 4 bancos
  principales con **fixtures de texto ficticias** (sin PDFs reales ni datos sensibles) en vez
  de los tests `Skipped` que dependían de archivos no versionados.
- **Refactor de los orquestadores gigantes** `UploadBankStatementHandler` y
  `GenerateJournalEntriesCommandHandler`: los métodos `Handle` (~280 y ~240 líneas) se
  descompusieron en pasos privados con nombre propio (validación de archivos, resolución de
  tenant, chequeo de cuota, presupuesto de deduplicación, carga de reglas, clasificación,
  proyección de líneas de asiento), cada uno cubierto por tests EF InMemory de integración.
- **Unificación de la firma de deduplicación** en el Dominio (`TransactionSignatureBuilder`):
  antes se interpolaba por separado en dos puntos de `UploadBankStatementHandler`, con riesgo
  de divergencia silenciosa; ahora hay una única fuente de verdad reutilizada por ambos.
- **Resiliencia de red con Polly** (`Microsoft.Extensions.Resilience`) en `SmtpEmailService`:
  timeout absoluto (30s) → retry con backoff exponencial + jitter (3 intentos) → timeout por
  intento (10s), con la fábrica de pipelines (`ResiliencePipelines`) centralizada y lista para
  reutilizarse en integraciones HTTP futuras (pasarelas de pago, APIs de terceros).
- **Cobertura crítica de frontend** con `HttpTestingController` (primer uso del patrón en este
  proyecto): el interceptor de errores (`error.interceptor.ts` — flujo 401 → silent-refresh →
  retry, y logout en sus dos variantes de fallo) y `reconciliation.service.ts` (estado y
  filtros, pila de deshacer/undo, polling del job de generación de asientos con
  `vi.useFakeTimers`).

---

# 🚀 Auditoría de Rendimiento y Escalabilidad — Estado

> Anotado el 2026-07-21. Fase ejecutada en rama `dev` sobre el informe de auditoría de
> Rendimiento y Escalabilidad (hallazgos críticos C-1/C-2/C-3 + P-10/P-11). Suite backend:
> **128 Passed / 65 Skipped / 0 Failed**. Frontend: `tsc --noEmit` y `ng build` sin errores.

## Ya completado (referencia rápida)

- **C-1 y C-2 (Upload asíncrono y memoria).** El OCR y el parseo de extractos ya no corren de
  forma síncrona en el hilo del request HTTP (riesgo de thread-starvation de toda la API y de
  OOM bajo subidas concurrentes). El endpoint de subida valida rápido (tamaño/extensión/magic
  bytes) y encola el procesamiento como job de Hangfire reutilizando el patrón MediatR ya
  existente en `GenerateJournalEntriesCommandHandler` —
  `backgroundJobClient.Enqueue<ISender>(sender => sender.Send(command))`
  ([TransactionEndpoints.cs:82](../backend/src/ContableAI.API/Endpoints/TransactionEndpoints.cs#L82)),
  sin necesidad de una clase de job dedicada. Se agregaron dos tablas en PostgreSQL:
  `StagedUploadFiles` (staging bytea del archivo entre el encolado y la ejecución del job) y
  `UploadJobResults` (resultado durable del job, leído por el endpoint de polling). El handler
  ya no depende de `ICurrentTenantService` (no hay `HttpContext` dentro de un job): el tenant
  viaja explícito en el propio command
  ([UploadBankStatementHandler.cs:179](../backend/src/ContableAI.Infrastructure/Features/Transactions/UploadBankStatementHandler.cs#L179)).
- **Protección de CPU/RAM.** `OcrStatementExtractor` eliminó la copia redundante a
  `MemoryStream`/`byte[]` y trabaja directo sobre el `Stream` recibido (PdfPig y PDFtoImage lo
  soportan nativo), y el OCR concurrente queda acotado con
  `SemaphoreSlim(2, 2)` ([OcrStatementExtractor.cs:33](../backend/src/ContableAI.Infrastructure/Services/OcrStatementExtractor.cs#L33))
  para que N jobs de Hangfire no saturen CPU/memoria del proceso como antes lo hacían N
  requests HTTP concurrentes.
- **Frontend Polling.** `reconciliation.service.ts` pollea el resultado del job (mismo patrón ya
  usado para la generación de asientos) y reproduce exactamente la UX anterior: toasts de
  éxito/error, modal de duplicados, ajuste automático de filtros por período importado, y el
  confirm-dialog de "¿reaplicar reglas?" — sin que el usuario note el cambio de arquitectura.
- **C-3 (Optimizaciones de lectura).** `GetTransactions` ya no materializa toda la tabla de
  transacciones filtrada del estudio solo para poblar los dropdowns de filtro; ahora son 3
  consultas `.Distinct()` independientes (cuenta / mes / año) resueltas en PostgreSQL
  ([TransactionEndpoints.cs:251](../backend/src/ContableAI.API/Endpoints/TransactionEndpoints.cs#L251)),
  apoyadas en el índice `IX_BankTransactions_CompanyId_Date` — escala con la cantidad de valores
  distintos, no con el volumen histórico de movimientos del estudio.
- **P-10 (Borrado masivo).** `DeleteAllTransactions` reemplazó `RemoveRange` +
  `SaveChangesAsync` por `ExecuteDeleteAsync`
  ([TransactionEndpoints.cs:531](../backend/src/ContableAI.API/Endpoints/TransactionEndpoints.cs#L531)),
  sin materializar ni trackear las filas a borrar. Como eso elude `AuditInterceptor` (que audita
  altas/bajas de `BankTransaction` una por una vía `SaveChangesAsync`), se compensa con una
  única fila manual en `AuditLogs` (`Deleted` / `BankTransaction` / `EntityId="BULK"` + cantidad
  borrada) en vez de perder el rastro o generar N filas individuales que serían puro ruido para
  un "clear all".
- **P-11 (Performance UI).** `TransactionGrid` (grilla de conciliación) pasa a
  `ChangeDetectionStrategy.OnPush`
  ([transaction-grid.ts:18](../frontend/src/app/features/reconciliation/components/transaction-grid/transaction-grid.ts#L18));
  el componente ya era 100% signals (`input`/`output`/`signal`/`computed`), que integran
  nativamente con OnPush sin cambios de comportamiento.

## ⏳ Pendiente (hallazgos del informe original aún no abordados)

- [ ] **P-1 · `AddDbContextPool`.** Sigue usándose `AddDbContext` — el pooling real requiere
  antes sacar la captura de `ICurrentTenantService` del constructor de `ContableAIDbContext`
  (incompatible con pooling tal como está hoy).
- [ ] **P-2 · Desnormalizar `StudioTenantId` en `BankTransaction`.** El Global Query Filter
  sigue anclado vía `JOIN` a `Company` en cada query de transacciones.
- [ ] **P-3 · Índice `pg_trgm`** para la búsqueda `ILike` sobre `Unaccent(Description)` (hoy
  sequential scan con wildcard inicial).
- [ ] **P-4 · Índice de respaldo para el sort por defecto** (`SortOrder`/`Date`) y para los
  demás campos ordenables de la grilla.
- [ ] **P-6 · Batchear `CheckManualSuggestionAsync`** en el bulk-update (N round-trips por
  grupo distinto de descripción/cuenta).
- [ ] **P-7 · Acotar `ProactiveLearningService`** por tenant/página (hoy trae 2 años de
  movimientos manuales de **todos** los estudios en cada corrida diaria) y moverlo a un cron de
  Hangfire en vez de `Task.Delay(24h)`.
- [ ] **P-8 · Backgroundear `AfipCombinationService`** (backtracking de combinaciones VEP) si en
  producción empieza a notarse en el tiempo de respuesta del endpoint.
- [ ] **P-9 · Configurar `WorkerCount`/colas de Hangfire** explícitamente, para que el upload
  asíncrono (ya acotado por su propio `SemaphoreSlim`) no compita sin límite por workers con
  otros jobs (cruce AFIP, generación de asientos) en el mismo proceso que la API.

---

## ☁️ Auditoría de Arquitectura e Infraestructura — Estado

Bloque de *Infrastructure & Observability Hardening* ejecutado sobre `dev`. Cierra los
hallazgos de la auditoría SRE (Riesgos de Infraestructura, Brechas de Observabilidad y
Recomendaciones de Resiliencia). Sin cambios funcionales de negocio; foco en
*cloud-readiness* para contenedores efímeros (Render) y trazabilidad de punta a punta.

### ✅ Resuelto

- [x] **R-1 · Resiliencia de conexión a PostgreSQL.** `UseNpgsql` con
  `EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: 10s)` + `CommandTimeout(30)`
  ([ServiceExtensions.cs](../backend/src/ContableAI.API/Extensions/ServiceExtensions.cs)).
  Los fallos transitorios / failovers del PostgreSQL gestionado se reintentan con backoff
  en vez de propagarse al usuario. No hizo falta `CreateExecutionStrategy`: no existe uso de
  `BeginTransaction()` en el código.
- [x] **R-2 / R-3 · Health checks reales con separación liveness/readiness.** Antes
  `AddHealthChecks()` no registraba ningún check y `/healthz` devolvía siempre `Healthy`
  aunque la base estuviera caída. Ahora `AddNpgSql` (tag `ready`) + dos endpoints:
  `/health/live` (liveness, sin dependencias externas → un micro-corte de la base no reinicia
  el contenedor) y `/health/ready` (readiness → PostgreSQL alcanzable)
  ([Program.cs](../backend/src/ContableAI.API/Program.cs)).
- [x] **O-2 · Logs aptos para contenedores efímeros.** Eliminado el *Serilog File Sink*
  (bootstrap y host logger); se perdía en cada redeploy y no se agregaba entre réplicas.
  Queda solo el Console sink (stdout estructurado), que la plataforma captura. Removido el
  paquete `Serilog.Sinks.File`.
- [x] **O-1 · Correlation ID de punta a punta.** Nuevo
  [`CorrelationIdMiddleware`](../backend/src/ContableAI.API/Middleware/CorrelationIdMiddleware.cs)
  (lee/genera y **sanitiza** el header `X-Correlation-Id`, lo publica en `LogContext`,
  `Activity`, `HttpContext.Items`, header de respuesta y un contexto ambiente `AsyncLocal`) +
  [`HangfireCorrelationIdFilter`](../backend/src/ContableAI.API/Common/HangfireCorrelationIdFilter.cs)
  (el filtro de cliente captura el ID vía `AsyncLocal` al encolar y el de servidor lo restaura
  al `LogContext` del worker). Cierra la traza Kestrel → MediatR → Hangfire.
- [x] **O-3 · `traceId` en `ProblemDetails`.** El `GlobalExceptionHandler` inyecta el
  Correlation ID como `Extensions["traceId"]`, correlacionando el error que ve el usuario con
  los logs del servidor. El frontend lo muestra en el toast de error 500 para reporte a soporte.
- [x] **R-5 (parcial P-7) · `ProactiveLearning` como cron de Hangfire.** Eliminado el
  `BackgroundService` con `Task.Delay(24h)` (corría en **cada** réplica → trabajo duplicado y
  *race* al insertar sugerencias; su timer se reiniciaba en cada redeploy). Reemplazado por
  [`ProactiveLearningJob`](../backend/src/ContableAI.Infrastructure/BackgroundJobs/ProactiveLearningJob.cs)
  agendado con `RecurringJob.AddOrUpdate(Cron.Daily)`: corrida única garantizada por el lock
  distribuido de Hangfire sobre PostgreSQL, scheduling durable. *Nota: falta aún el acotado por
  tenant/página de P-7.*
- [x] **B-2 · Cloud readiness.** Eliminados los endpoints `suggestions-debug` de
  `RulesEndpoints.cs` que leían/escribían `logs/suggestions-debug.jsonl` en disco local
  (código muerto que acoplaba falsamente al file system efímero).
- [x] **B-1 · Estrategia de backup/retención documentada.** Nuevo
  [`RUNBOOK_DB.md`](RUNBOOK_DB.md): corrige la asunción original ("Postgres gestionado en
  Render") — el motor real es **Neon**, hoy en plan Free. Define política de retención
  (PITR nativo de Neon + `pg_dump` semanal como defensa en profundidad), RPO/RTO objetivo
  para la fase Beta (≤24h / ≤4h) y el procedimiento paso a paso de restauración ante
  desastre. *Nota: es la política documentada, no automatización — el simulacro de
  restauración real y la verificación de la ventana de retención vigente en Neon quedan
  como acciones pendientes explícitas dentro del propio runbook.*

### 🔧 Acciones operativas manuales (fuera del código)

- [ ] **CRÍTICO — Render → Settings → Health Check Path:** cambiar de `/healthz` a
  **`/health/ready`**. Si no se actualiza, Render seguirá pegándole a la ruta vieja (ahora 404)
  y marcará el servicio como no saludable, bloqueando el deploy.
- [ ] Verificar `/health/live` y `/health/ready` tras el primer deploy con estos cambios.
- [ ] Ver `RUNBOOK_DB.md` §6 para las acciones pendientes de la política de backups
  (verificar ventana de retención de Neon, simulacro de restauración).

---

# 🎨 Auditoría de Usabilidad y Accesibilidad (UX/A11y) — Estado

> Anotado el 2026-07-21. Fase ejecutada en rama `dev`. Los fixes críticos de la conciliación
> quedaron aplicados en el commit `1e8cc4e`; el resto de los hallazgos queda registrado como
> pendiente.

## Ya completado (commit `1e8cc4e`)

- **UX-1 · Overlay bloqueante eliminado.** El polling de estado de los jobs (subida de
  extractos y generación de asientos) usa el contexto `SKIP_LOADING`, de modo que las
  requests periódicas ya no disparan el overlay global de carga: la UI queda usable mientras
  el job corre en segundo plano
  ([transaction.ts](../frontend/src/app/core/services/transaction.ts),
  [journal-entry.service.ts](../frontend/src/app/core/services/journal-entry.service.ts)).
- **UX-2 / UX-3 · Estado `_isGenerating` corregido.** El flag se mantiene activo durante todo
  el ciclo del job (no solo durante la request inicial), lo que deshabilita el botón de
  generación mientras hay un job en vuelo y evita el **doble asentamiento** por doble clic
  ([reconciliation.service.ts](../frontend/src/app/features/reconciliation/reconciliation.service.ts)).
- **FL-1 · Dropzone acumulativa.** La zona de subida de extractos acumula los archivos de
  selecciones sucesivas en vez de reemplazar la lista anterior
  ([upload-zone.ts](../frontend/src/app/features/reconciliation/components/upload-zone/upload-zone.ts)).

## ⏳ Pendiente

- [ ] **A11y-1 a A11y-6 · Hallazgos de accesibilidad.** Barrido completo de accesibilidad:
  roles/labels ARIA en controles interactivos, foco visible y orden de tabulación, contraste
  de colores, navegación por teclado en la grilla de conciliación y en los modales, textos
  alternativos, y anuncio de cambios de estado (toasts/polling) a lectores de pantalla.
- [ ] **Timeout del polling.** El polling de jobs (subida de extractos / generación de
  asientos) no tiene tope: si el job nunca escribe resultado, el front pollea indefinidamente.
  → Definir timeout con mensaje claro de "reintentar / contactar soporte".
- [ ] **Mensajes de error orientados al contador.** Reemplazar errores técnicos genéricos por
  mensajes accionables en el dominio del usuario (qué pasó, qué hacer, con qué archivo/fila).
- [ ] **Fricción de la dropzone de AFIP.** La subida de comprobantes AFIP no tiene la misma
  ergonomía que la de extractos (acumulación de archivos, feedback de progreso por archivo).
  → Unificar comportamiento con la dropzone principal.

---

# ⚖️ Auditoría de Cumplimiento Legal y Privacidad — Estado

> Anotado el 2026-07-21. Fase de inspección (solo lectura) ejecutada en rama `dev`. **Ningún
> hallazgo fue remediado aún: todo el bloque queda pendiente.** Veredicto de licencias: no hay
> GPL/AGPL en ninguna dependencia directa (backend ni frontend).

## ⏳ Pendiente — Riesgos de Privacidad de Datos (P-1 a P-6)

- [ ] **🔴 P-1 · Sin mecanismo formal de cierre de cuenta (derecho al olvido).** No existe
  endpoint, job ni script que un estudio pueda usar para solicitar el borrado de su cuenta.
  El único hard-delete es `DELETE /api/admin/users/{id}` (solo SystemAdmin,
  [AdminHandlers.cs](../backend/src/ContableAI.Infrastructure/Features/Admin/AdminHandlers.cs)),
  y su cascada es **incompleta**: sobreviven `AuditLogs` (con email del usuario),
  `UploadJobResults`, las reglas a nivel estudio (`CompanyId == null`) y los `RefreshTokens`.
  → Diseñar un comando `DeleteStudioTenant` que cubra todas las tablas del tenant, con
  anonimización del email en auditoría y registro del pedido y de la ejecución.
- [ ] **🔴 P-2 · Soft-delete inseguro de `Company`.** `DELETE /api/companies/{id}` solo setea
  `IsActive = false`: transacciones, asientos, reglas y CUIT quedan vivos en la base
  indefinidamente. No hay `DeletedAt` ni job de purga diferida, y `GetCompanyHandler` sigue
  devolviendo por ID empresas "borradas".
  → Agregar marca temporal de borrado + política de purga documentada (p. ej. hard-delete a
  los 90 días).
- [ ] **🟠 P-3 · Datos residuales del hard-delete admin.** (Detalle de P-1: `AuditLogs`,
  `UploadJobResults`, reglas de estudio, `RefreshTokens` huérfanos.) Cubierto por el diseño
  del comando de cierre de cuenta.
- [ ] **🟠 P-4 · `UploadJobResults` acumula datos financieros sin retención.** El JSON completo
  del resultado de cada procesamiento de extracto se persiste (jsonb, por `StudioTenantId`) y
  **nunca se borra** — solo existe el `Add`.
  → Job recurrente de purga por antigüedad (p. ej. > 7 días).
- [ ] **🟡 P-5 · `StagedUploadFiles` sin red de seguridad.** El handler borra la fila al leer
  los bytes (bien), pero si el job de Hangfire nunca ejecuta (reintentos agotados, deploy a
  mitad de cola), el archivo completo queda huérfano en la base para siempre.
  → Incluir purga de huérfanos por `CreatedAt` en el mismo job de retención de P-4.
- [ ] **🟡 P-6 · `AuditLogs` sin política de retención documentada.** Retención larga es
  defendible en una plataforma financiera, pero debe documentarse como política e incluir la
  seudonimización del email tras el cierre de cuenta.

## ⏳ Pendiente — Riesgos de Licenciamiento (L-1 y L-4)

- [ ] **🔴 L-1 · Procedencia opaca de `UglyToad.PdfPig 1.7.0-custom-5`.** La versión referida en
  [ContableAI.Infrastructure.csproj](../backend/src/ContableAI.Infrastructure/ContableAI.Infrastructure.csproj)
  no existe en nuget.org (las oficiales son 0.1.x), el repo no tiene `nuget.config` ni feed
  local, y el nuspec cacheado no declara licencia, autor ni URL de repositorio (solo un hash
  de commit). Doble riesgo: cumplimiento (el upstream Apache 2.0 exige conservar
  LICENSE/NOTICE, imposible de demostrar) y cadena de suministro (el restore solo funciona en
  máquinas con caché; un entorno limpio falla o podría resolver un paquete ajeno).
  → Localizar el fork de origen y publicarlo en un feed privado con su LICENSE/NOTICE, o
  migrar a la versión oficial de PdfPig. **Resolver antes de cualquier due diligence.**
- [ ] **🟡 L-4 · Gobernanza de MediatR.** La versión en uso (12.4.1) es Apache 2.0, pero a
  partir de v13 MediatR pasó a licenciamiento comercial.
  → Fijar la versión en < 13 y establecer como política no subir de major en dependencias
  sin revisión de licencia previa.