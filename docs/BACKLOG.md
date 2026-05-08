# ContableAI — Backlog & Roadmap Unificado

> **Documento único de trabajo.** Combina feedback de cliente, bugs reportados, mejoras de UX, features planificadas y roadmap estratégico.
> Última actualización: 2026-05-07 (Sesión: CONFIG-01, CONFIG-02, RESET-01 — Separación de entornos, limpieza de git, reset de BD local y Neon)

## 🏗️ ESTADO DEL PRODUCTO (Baseline — Q1 2026)

**Stack:** `.NET 10` + `Angular 21` + `PostgreSQL` (Clean Architecture)

**Funcionalidades activas en producción:**
- Parsers multi-banco: BBVA, Galicia, Santander, Macro, Nación, MercadoPago, Ualá, Credicoop, PDF genérico, **Banco Ciudad** ✨
- Motor de reglas keyword-based con 245+ reglas globales predefinidas.
- Cruce AFIP / VEP contra extractos bancarios via `AfipMatchingJob` (Hangfire, tolerancia ±2 días) — **Persistencia de vouchers + auto-trigger al subir extracto** ✨
- Generación de asientos de partida doble con validación de períodos cerrados — **Debe/Haber ordenado** ✨
- Exportación multi-formato (CSV, Excel, Holistor, Bejerman).
- Multi-tenancy (estudio → empresa → usuario) con planes Free / Pro / Enterprise — **Cuentas soft-delete** ✨
- Dashboard de KPIs con filtros por mes y año — **Límites accesibles correctamente** ✨
- Auditoría completa de todas las operaciones.
- **Aprendizaje Proactivo:** Detección en tiempo real de patrones manuales repetidos (≥3 veces / 90 días) → sugerencia de regla con 1 click. Panel visible en página de Conciliación y Reglas. ✨

---

## 🚨 BUGS — PRIORIDAD ALTA

*(Nota: Los bugs BUG-01 a BUG-14 fueron resueltos en sesiones anteriores y marcados como completados en el histórico).*

---

## 🎨 MEJORAS DE UX — PRIORIDAD MEDIA-ALTA

### UX-01b · Selector de cuenta con input de búsqueda visible (combobox)
- **Reportado por:** Seba Presman
- **Descripción:** El selector nativo `<select>` esconde lo que el usuario tipea. Se requiere un combobox con input visible (como un autocomplete) para filtrar a la vista.
- **Fix:** Reemplazar el `<select>` nativo por un componente combobox (Angular CDK Listbox + input, o ng-select). Mantener navegación por teclado. Validar si se puede reusar el componente del "Modo Excel" (P0-1).
- **Esfuerzo:** 3-5 h
- **Estado:** PENDIENTE — Prioridad ALTA

### UX-04 · Sugerencias Proactivas con "Fuzzy Matching"
- **Descripción:** Actualmente el sistema de sugerencias requiere coincidencias exactas. Textos como "Cheque Galicia 001" y "Cheque Galicia 002" no disparan la sugerencia aunque comparten el mismo patrón base.
- **Fix:** Evaluar e implementar lógica de "fuzzy matching" o sanitización previa (ej: ignorar números al final de la descripción) para mejorar la tasa de detección del Aprendizaje Proactivo.
- **Estado:** PENDIENTE — Prioridad MEDIA

---

## 🚀 FEATURES NUEVAS Y CONFIGURACIÓN — PRIORIDAD MEDIA

### CONFIG-01 · Separación de Entornos (Local vs Producción)
- **Descripción:** Facilitar el desarrollo evitando conflictos de Connection Strings al cambiar entre la base de datos local (Docker) y Neon PostgreSQL.
- **Fix:** `appsettings.json` limpio sin secrets. `appsettings.Development.json` gitignored con Docker local. Producción vía env vars en Render (`ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Smtp__Password`).
- **Estado:** ✅ Completado — 2026-05-07

### CONFIG-02 · Limpieza y Actualización de `.gitignore`
- **Descripción:** Evitar subir archivos basura, de compilación o configuraciones sensibles al repositorio.
- **Fix:** `.gitignore` actualizado con `backend/**/logs/`. Logs históricos removidos del tracking con `git rm --cached`.
- **Estado:** ✅ Completado — 2026-05-07

### CONFIG-03 · Git Flow & Branching Environments (Git/Neon/Render)
- **Descripción:** Establecer un flujo de trabajo formal de ramas (ej. `main` para producción, `dev` para staging/desarrollo) sincronizado con ramas equivalentes en la base de datos de Neon y entornos separados en Render.
- **Fix:** Definir estrategia de ramas, vincular ramas de GitHub con las bases de datos de Neon correspondientes, y configurar el auto-deploy en Render por rama (Production vs. Preview).
- **Estado:** PENDIENTE — Prioridad MEDIA (Ejecutar *después* del despliegue estable a producción).

---

## 🧪 HALLAZGOS DE TESTING — UX & QA

| ID | Tipo | Descripción | Prioridad | Propuesta / Estado |
|---|---|---|---|---|
| **TEST-06** | UX/Feature | **Rediseñar flujo "Aplicar regla"** | **ALTA** | ✅ Completado — flujo invertido, prioriza creación prellenada. |
| **TEST-07** | Feature | Sugerencia auto de regla | ALTA | ✅ Completado — detección en tiempo real. |
| **TEST-10** | Feature | Toggle Activa/Inactiva reglas | MEDIA | ✅ Completado — soft-delete implementado. |
| **TEST-01** | UX | Import sin filtro poco claro | MEDIA | ✅ Completado — Movido a "Opciones avanzadas" colapsable. |
| **TEST-11** | UX | Rediseño visual de Cuentas | MEDIA | ✅ Completado — Densidad visual unificada. |
| **TEST-15** | Prod. | Dashboard de bajo valor | BAJA | ✅ Completado — Desactivado temporalmente y redirigido a `/`. |

---

## ⏳ TAREAS PENDIENTES

- **UX-01b** – Combobox visible para selector de cuentas – PENDIENTE.
- **UX-04** – Sugerencias con "Fuzzy Matching" (ignorando números) – PENDIENTE.
- ✅ **CONFIG-01** – Separación de `appsettings` (Development/Production) – Completado 2026-05-07.
- ✅ **CONFIG-02** – Revisión de `.gitignore` – Completado 2026-05-07.
- ✅ **RESET-01** – BD local (Docker) y Neon reseteadas desde cero, migraciones aplicadas – Completado 2026-05-07.
- **CONFIG-03** – Implementar manejo de ramas sincronizado (Git/Neon/Render) post-producción – PENDIENTE.
- **[Render]** – Configurar env vars en dashboard: `ASPNETCORE_ENVIRONMENT=Production`, `ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Smtp__Password` – PENDIENTE (manual).

---

## 🗺️ ROADMAP ESTRATÉGICO

### P0 — Fundaciones del Core (Q2 2026)
- ✅ **[P0-1] Experiencia "Modo Excel"**
- ✅ **[P0-2] Aprendizaje Proactivo**
- ✅ **[P0-3] Conciliación Mágica Completa**

### P1 — Crecimiento y Retención (Q3 2026)
- ✅ **[P1-1] Reglas Globales de Estudio**
- ✅ **[P1-2] Onboarding Interactivo (FTUE)**
- ⏳ **[P1-3] Landing Page Comercial:** Sitio SEO friendly (Astro/Next) para captación de leads y pasarela de pagos.

### P2 — Diferenciación Estratégica (Q4 2026)
- ⏳ **[P2-1] Portal del Cliente:** Acceso tokenizado sin login para subida de extractos por parte de PyMEs.
- ⏳ **[P2-2] Cash Flow Predictivo:** Job asíncrono para predecir débitos y detectar anomalías tributarias/outliers.
- ⏳ **[P2-3] API Pública + Webhooks:** Endpoints asegurados para integración directa con ERPs Cloud (Holistor, Bejerman).

---

## 🛠️ DEUDA TÉCNICA (Bloqueadores)

| Componente | Desbloquea |
|---|---|
| **Notificaciones In-App** | Aprendizaje Proactivo (P0-2), Portal del Cliente (P2-1) |
| **Websockets / Server-Sent Events** | Estado en tiempo real para Portal del Cliente (P2-1) |
| **Versionado de API (`/api/v1/`)** | API Pública (P2-3) |
| **Cobertura Tests de Integración >70%** | API Pública (P2-3) |

---

## 📋 PLAN DE ACCIÓN INMEDIATO (Siguientes Sprints)

### Historial de sesiones completadas
- ✅ **BUG-07:** Hangfire/Jobs para extractos masivos.
- ✅ **P0-3 / P0-2:** Ingesta AFIP y Aprendizaje Proactivo HTTP.
- ✅ **[P1-1] Completo:** BD, Cascada Multi-Empresa, UI `/studio-rules`.
- ✅ **[P1-2] Completo:** `TourService`, demo data (CSV Galicia bundleado).
- ✅ **BUG-10:** Solucionado error 500 al subir comprobantes AFIP duplicados (unique constraint fix).
- ✅ **BUG-11:** Arreglado el filtro numérico de montos (exacto, min, max) en la API.
- ✅ **BUG-12:** Corregida la exportación que devolvía 404 al filtrar por mes.
- ✅ **BUG-13:** Confirmado el orden correcto (Debe/Haber) en asientos visuales y exportaciones.
- ✅ **BUG-14:** Botones de toggle habilitados para reglas de sistema; eliminar restringido a reglas propias.
- ✅ **UX-02:** Select estricto de cuentas contables al crear/editar reglas (se eliminó el input libre).
- ✅ **UX-03:** Descripciones vacías ahora muestran un texto "Sin descripción" genérico e itálico.
- ✅ **TEST-15:** Dashboard desactivado y oculto del menú lateral, redirigiendo a la grilla principal.

- ✅ **CONFIG-01 & 02:** `appsettings` separados, `.gitignore` actualizado, logs removidos del tracking.
- ✅ **RESET-01:** BD local (Docker) y Neon dropeadas y recreadas desde migración `InitCleanDb`. Seed automático al iniciar.

### Próximos (Siguiente sesión)
1. **[Render]** Configurar las env vars de producción en el dashboard de Render (`ASPNETCORE_ENVIRONMENT`, `ConnectionStrings__DefaultConnection`, `Jwt__Key`, `Smtp__Password`).
2. **CONFIG-03:** Implementar Git Flow (rama `dev`, Neon branch staging, Render preview).
3. **P1-3:** Landing Page Comercial (Astro/Next).

---

## 📈 MÉTRICAS DE ÉXITO

| Iniciativa | Métrica | Target |
|---|---|---|
| **Conciliación Mágica** | % VEPs cruzados automáticamente | ≥ 95% |
| **Aprendizaje Proactivo** | Reducción clasificaciones manuales repetidas | -40% en 60 días |
| **Modo Excel** | Tiempo clasificación de 100 tx | < 8 min |
| **Reglas Globales** | Tiempo de setup empresa nueva | < 5 min |
| **Onboarding** | Tasa de activación en 7 días | ≥ 60% |
| **Landing Page** | Registros orgánicos mensuales (M3) | ≥ 50/mes |
| **Portal del Cliente**| NPS de clientes del estudio | ≥ 8/10 |

---

## ⚙️ POST-ROADMAP (POST-Q4 2026)
- Optimización de índices PostgreSQL y queries complejas.
- Caching distribuido (Redis) para limits y configs.
- Límite de Rate Limiting por tenant.
- APM (Datadog/Grafana) para latencia p99 < 500ms y Uptime 99.9%.



ANALISIS COMPELTOD E LA APP CON CUSTIONES CRITICAS Y TODO 

ELIMINACION DE COSAS SIN USO Y REDUNDANTES 

