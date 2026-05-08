# ContableAI — Backlog & Roadmap Unificado

> **Documento único de trabajo.** Combina feedback de cliente, bugs reportados, mejoras de UX, features planificadas y roadmap estratégico.
> Última actualización: 2026-05-08 (Sesión: Auditoría profunda pre-producción, Fix Vercel/Render, Git Flow)

## 🏗️ ESTADO DEL PRODUCTO (Baseline — Q1 2026)

**Stack:** `.NET 10` + `Angular 21` + `PostgreSQL` (Clean Architecture)

**Funcionalidades activas en producción:**
- Parsers multi-banco: BBVA, Galicia, Santander, Macro, Nación, MercadoPago, Ualá, Credicoop, PDF genérico, Banco Ciudad ✨
- Motor de reglas keyword-based con 245+ reglas globales predefinidas.
- Cruce AFIP / VEP contra extractos bancarios via `AfipMatchingJob` (Hangfire, tolerancia ±2 días) — Persistencia de vouchers + auto-trigger al subir extracto ✨
- Generación de asientos de partida doble con validación de períodos cerrados — Debe/Haber ordenado ✨
- Exportación multi-formato (CSV, Excel, Holistor, Bejerman).
- Multi-tenancy (estudio → empresa → usuario) con planes Free / Pro / Enterprise — Cuentas soft-delete ✨
- Dashboard de KPIs con filtros por mes y año — Límites accesibles correctamente ✨
- Auditoría completa de todas las operaciones.
- Aprendizaje Proactivo: Detección en tiempo real de patrones manuales repetidos (≥3 veces / 90 días) → sugerencia de regla con 1 click.

---

## 🚨 BUGS — PRIORIDAD ALTA

*(Nota: Los bugs BUG-01 a BUG-14 y el fix de entornos en Vercel fueron resueltos y desplegados a producción).*

---

## 🎨 MEJORAS DE UX — PRIORIDAD MEDIA-ALTA

### UX-01b · Selector de cuenta con input de búsqueda visible (combobox)
- **Reportado por:** Seba Presman
- **Descripción:** El selector nativo `<select>` esconde lo que el usuario tipea. Se requiere un combobox con input visible.
- **Estado:** PENDIENTE — Prioridad ALTA

### UX-04 · Sugerencias Proactivas con "Fuzzy Matching"
- **Descripción:** Actualmente el sistema de sugerencias requiere coincidencias exactas. Implementar lógica para ignorar números al final de las descripciones.
- **Estado:** PENDIENTE — Prioridad MEDIA

---

## 🚀 FEATURES NUEVAS Y CONFIGURACIÓN

### AUDIT-01 · Auditoría y Limpieza Profunda Pre-Producción
- **Descripción:** Escaneo de vulnerabilidades, memory leaks y código muerto.
- **Fix:** `TakeUntilDestroyed` en todo Angular, eliminación de `config.json` dinámico (Vercel fix), adición de `.AsNoTracking()` en EF Core, y rotación de contraseñas (GitGuardian fix).
- **Estado:** ✅ Completado — 2026-05-08

### CONFIG-03 · Git Flow & Branching Environments (Git/Neon/Render)
- **Descripción:** Establecer un flujo de trabajo formal de ramas (`main` para producción, `dev` para staging/desarrollo).
- **Fix:** Entorno local aislado (`appsettings.Development.json` ignorado apuntando a Neon Dev), Render apuntando a Neon Prod mediante variables de entorno seguras.
- **Estado:** ✅ Completado — 2026-05-08

---

## ⏳ TAREAS PENDIENTES

- **UX-01b** – Combobox visible para selector de cuentas – PENDIENTE.
- **UX-04** – Sugerencias con "Fuzzy Matching" (ignorando números) – PENDIENTE.
- **P1-3** – Landing Page Comercial (Astro/Next) para captación de leads – PENDIENTE.

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

## 📋 PLAN DE ACCIÓN INMEDIATO (Siguientes Sprints)

### Historial de sesiones recientes
- ✅ **BUG-07 a BUG-14:** Fixes masivos de performance y filtros.
- ✅ **TEST-01 a TEST-15:** Pulido completo de UX de cara al cliente.
- ✅ **CONFIG-01, 02 y 03:** Entornos separados, `.gitignore` seguro y Git Flow implementado entre Github, Neon y Render.
- ✅ **AUDIT-01:** Prevención de fugas de memoria en RxJS, mitigación de logs sensibles, fix de CORS/Localhost en Vercel.

### Próximos (Siguiente sesión)
1. **Verificación de Producción:** Validar login y test de carga en `contable-ai-sandy.vercel.app`.
2. **UX-01b:** Implementar combobox visible en selectores de cuentas.
3. **P1-3:** Iniciar desarrollo de la Landing Page Comercial.