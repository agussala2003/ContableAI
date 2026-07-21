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

## Ya completado — Vulnerabilidades Bajas (B-1) y observaciones de código

- [x] **B-1a · `AllowedHosts` restringido (2026-07-21).** El base
  [appsettings.json](../backend/src/ContableAI.API/appsettings.json) quedó fijado a
  `contableai-api.onrender.com;localhost;127.0.0.1` (el host de producción según COMMANDS.md,
  más loopback para corridas locales en modo prod); Development lo abre a `*` en su override.
  Si algún día se agrega un dominio custom, sobreescribir con la env var `AllowedHosts` en
  Render sin tocar código.
- [x] **B-1b · Enumeración de emails en registro (2026-07-21).** `register` (invitados, flujo
  asíncrono sin login) ahora es de **respuesta constante**: con email duplicado devuelve el
  mismo `202` y el mismo mensaje que el alta real, sin crear nada — indistinguible para un
  atacante. `register-studio` conserva el `409` como **trade-off aceptado y documentado en el
  código**: es self-serve con login inmediato (devuelve JWT), y una respuesta genérica exigiría
  un paso de verificación por email que hoy no existe; el mensaje pasó a ser neutral y orienta
  al usuario legítimo hacia login/recupero. Mitigan: `forgot-password` y `register` constantes.
- [x] **B-1c · Token de reset con CSPRNG y hash en BD (2026-07-21).** Nuevo
  [PasswordResetTokens.cs](../backend/src/ContableAI.Infrastructure/Services/PasswordResetTokens.cs)
  (mismo esquema que los refresh tokens de A-3): token de **256 bits** de
  `RandomNumberGenerator`, en `User.PasswordResetToken` se persiste **solo el hash SHA-256**,
  y la verificación compara hashes en tiempo constante (`FixedTimeEquals`). Corregido en los
  **dos** generadores: `ForgotPasswordHandler` y también `SendAdminPasswordResetHandler`
  (AdminHandlers), que la auditoría no listaba pero tenía el mismo `Guid.NewGuid()`. Los
  tokens legados en claro fallan la verificación y expiran solos (≤ 24 h). Tests en
  [PasswordResetSecurityTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/PasswordResetSecurityTests.cs).
- [x] **Inconsistencia del tenant del admin (2026-07-21).** `SeedExtensions` ahora usa
  `TenantConstants.System` (`"SYSTEM"`), igual que el endpoint de seed-admin. Nota: bases ya
  sembradas conservan el admin con `"system"` en minúsculas — inocuo (el bypass de filtros es
  por rol, no por string) y no amerita migración de datos.

## ⏳ Observaciones menores (no bloqueantes, para el radar)

- [ ] **Cookie de refresh cross-site (A-3).** Es `SameSite=None` (Vercel ↔ Render son cross-site);
  navegadores que bloquean cookies de terceros podrían desloguear al recargar.
  → Validar en staging: login → reload → sigue logueado. (Requiere entorno desplegado; no es
  resoluble desde código.)

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

## Ya completado — Privacidad de Datos (P-1 a P-6)

- [x] **🔴 P-1 · Cierre formal de cuenta implementado (2026-07-21).** Nuevo comando
  `DeleteStudioTenantCommand` + endpoint `DELETE /api/admin/tenants/{studioTenantId}` (solo
  `SystemAdmin`, con `?reason=` opcional). Ejecuta dentro de una transacción (execution
  strategy compatible con el retry de R-1) la cascada completa del tenant vía
  [StudioTenantPurger.cs](../backend/src/ContableAI.Infrastructure/Features/Admin/StudioTenantPurger.cs):
  usuarios, `RefreshTokens`, empresas (incluidas las soft-deleted), transacciones, asientos y
  líneas, vouchers AFIP, reglas de empresa **y de estudio**, sugerencias, plan de cuentas
  propio, períodos cerrados, `UploadJobResults` y purga defensiva de `StagedUploadFiles`
  huérfanos (> 24 h, adelanto de P-5). Los `AuditLogs` NO se borran: se **seudonimizan**
  (email → `deleted-user-{userId}@anonymized.local`, diffs `Changes` vaciados — pueden
  contener importes y CUITs), y la ejecución queda registrada con un AuditLog de cierre
  (solicitante, motivo y conteo por tabla, que también devuelve la API como evidencia).
  Deletes set-based (`ExecuteDeleteAsync`/`ExecuteUpdateAsync`, P-10) en el proveedor
  relacional. Tests de integración en
  [DeleteStudioTenantHandlerTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/DeleteStudioTenantHandlerTests.cs).
- [x] **🔴 P-2 · Soft-delete de `Company` corregido (2026-07-21).** `Company.DeletedAt`
  (migración `AddCompanyDeletedAt`) se estampa en el DELETE de forma idempotente (un segundo
  DELETE no corre la ventana), y `GetCompanyHandler` ya no devuelve por ID empresas dadas de
  baja (404, igual que una inexistente). **Política de purga diferida:** las empresas con
  `DeletedAt` > 90 días deben hard-deletearse; la ejecución recurrente queda a cargo del job
  de retención de P-4 (pendiente), que ya tiene en `DeletedAt` la marca que necesita. Tests en
  [CompanySoftDeleteTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/CompanySoftDeleteTests.cs).
- [x] **🟠 P-3 · Residuos del hard-delete admin eliminados (2026-07-21).** `DeleteUserHandler`
  ahora delega en el mismo `StudioTenantPurger` cuando cae el último usuario del estudio
  (cascada idéntica al cierre formal, sin residuos), y cuando quedan otros usuarios borra los
  `RefreshTokens` del usuario y seudonimiza su email en `AuditLogs`.

- [x] **🟠 P-4 · Retención de `UploadJobResults` implementada (2026-07-21).** Nuevo
  [DataRetentionJob](../backend/src/ContableAI.Infrastructure/BackgroundJobs/DataRetentionJob.cs)
  (recurring job diario de Hangfire, `data-retention`, mismo patrón que R-5: lock distribuido,
  corrida única entre réplicas) que purga con `ExecuteDeleteAsync` (P-10) los resultados con
  más de **30 días** (configurable en la sección `DataRetention` de appsettings —
  [DataRetentionOptions.cs](../backend/src/ContableAI.Infrastructure/Options/DataRetentionOptions.cs)).
  El mismo job ejecuta la **purga diferida de P-2**: empresas con `DeletedAt` > 90 días se
  hard-deletean en cascada completa (`StudioTenantPurger.PurgeCompaniesAsync`, compartida con
  el cierre de cuenta). Tests en
  [DataRetentionJobTests.cs](../backend/tests/ContableAI.Tests/Infrastructure/DataRetentionJobTests.cs).
- [x] **🟡 P-5 · Red de seguridad de `StagedUploadFiles` implementada (2026-07-21).** El
  `DataRetentionJob` purga a diario los archivos de staging con más de **24 h** (configurable),
  cubriendo jobs de Hangfire que nunca ejecutaron (reintentos agotados, deploy a mitad de
  cola). Se suma a la purga defensiva que ya hacía el cierre de cuenta (P-1).
- [x] **🟡 P-6 · Política de retención de `AuditLogs` documentada (2026-07-21).** Formalizada
  en [RETENCION_DATOS.md](RETENCION_DATOS.md): retención de **5 años** por trazabilidad
  financiera, seudonimización inmediata e irreversible ante cierre de cuenta o borrado de
  usuario (implementada en P-1), registro del cierre como evidencia, borrado definitivo como
  tarea anual con revisión humana, e interacción con la ventana de PITR de Neon (los datos
  purgados persisten en backups hasta que la ventana expira). El documento incluye además la
  tabla completa de retención por clase de dato (staging, resultados de jobs, empresas
  soft-deleted, tokens).

## Ya completado — Riesgos de Licenciamiento (L-1 y L-4)

- [x] **🔴 L-1 · Migrado al paquete oficial `PdfPig 0.1.15` (2026-07-21).** Se eliminó
  `UglyToad.PdfPig 1.7.0-custom-5` de
  [ContableAI.Infrastructure.csproj](../backend/src/ContableAI.Infrastructure/ContableAI.Infrastructure.csproj)
  y se lo reemplazó por el paquete oficial del proyecto, que publica bajo el **ID `PdfPig`**
  (autores `UglyToad`, owners `EliotJones`/`BobLd`/`PdfPig`, Apache 2.0, ~26,5M descargas,
  última estable 0.1.15). El paquete oficial conserva el namespace `UglyToad.PdfPig`, por lo
  que no hubo cambios de código: `PdfDocument.Open(Stream)`, `GetPages()`, `GetWords()`,
  `BoundingBox` y `page.Text` funcionan idéntico, se mantienen los `Stream` en memoria y el
  `SemaphoreSlim(2,2)` del OCR. Suite completa validada contra el dataset real de PDFs
  (193/193 en verde, extractos y VEPs incluidos).
  **Hallazgo adicional (más grave que lo auditado):** el ID `UglyToad.PdfPig` en nuget.org
  NO es del proyecto oficial — pertenece a un tercero no verificado (`grinay`), sin licencia,
  sin autor real ni URL de repo, con solo dos versiones prerelease (`1.7.0-custom-5` y
  `0.1.9-alpha001-patch1`). Es decir, no era solo un problema de caché local: cualquier
  restore limpio descargaba ese paquete de procedencia opaca desde nuget.org. Recomendado:
  purgar `~/.nuget/packages/uglytoad.pdfpig*` en las máquinas del equipo y considerar
  bloquear el prefijo `UglyToad.*` vía `nuget.config` (`packageSourceMapping`) para evitar
  reintroducciones accidentales.

- [x] **🟡 L-4 · Gobernanza de MediatR — versión fijada por debajo de v13 (2026-07-21).**
  MediatR 12.4.1 (Apache 2.0) quedó fijado con rango cerrado `[12.4.1, 13.0.0)` en
  [ContableAI.Application.csproj](../backend/src/ContableAI.Application/ContableAI.Application.csproj),
  con comentario explicando el motivo: a partir de v13 MediatR pasó a licenciamiento
  comercial, y el rango impide que un upgrade automático (Dependabot, `dotnet add package`
  sin versión, etc.) cruce el major sin revisión de licencia previa. No hace falta pinear
  `MediatR.Extensions.Microsoft.DependencyInjection`: desde v12 el registro DI viene
  integrado en el paquete principal y no existe otra referencia MediatR en la solución.
  Restore verificado (resuelve exactamente 12.4.1) y suite completa en verde (193/193
  contra el dataset real de PDFs). **Política:** no subir de major en dependencias sin
  revisión de licencia previa.