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

### FIX-A · Cuentas duplicadas por casing al cruzar AFIP
- **Reportado por:** Seba Presman (charla 2026-06-07)
- **Descripción:** Una cuenta cargada a mano ("cargas sociales") y la misma cuenta asignada por el cruce AFIP ("Cargas Sociales") quedaban como dos cuentas distintas. Además, la búsqueda por cuenta no traía todas las filas (comparación case-sensitive en Postgres).
- **Fix:** Nuevo `AccountNameResolver` que canonicaliza el nombre contra el plan de cuentas (case-insensitive) en todas las escrituras (asignación manual single/bulk + cruce AFIP). Creación de cuentas case-insensitive y filtro de búsqueda case-insensitive. Seed de las cuentas destino del cruce AFIP. **Normalización en lote** (acción admin `POST /api/admin/normalize-accounts` + botón en Admin): reescribe `BankTransactions.AssignedAccount` a la forma canónica para limpiar data legacy con casing mixto (idempotente, no toca reglas ni asientos históricos).
- **Estado:** ✅ Completado — 2026-06-08 (rama `dev`)

---

## 🎨 MEJORAS DE UX — PRIORIDAD MEDIA-ALTA

### UX-01b · Selector de cuenta con input de búsqueda visible (combobox)
- **Reportado por:** Seba Presman
- **Descripción:** El selector nativo `<select>` esconde lo que el usuario tipea. Se requiere un combobox con input visible.
- **Fix:** Nuevo componente reutilizable `AccountCombobox` (`shared/components/account-combobox/`): input de texto visible + dropdown filtrable (substring, accent/case-insensitive), navegación con teclado (↑↓ Enter Esc), click-outside y texto libre opcional. Reemplaza el `<select>` nativo del formulario de reglas (`rule-form-slideover`) y el input de asignación masiva del grid.
- **Estado:** ✅ Completado — 2026-06-08. Nota: la edición inline por fila del grid se dejó con su `input+datalist` actual a propósito — ya muestra el texto tipeado y convertirla al combobox arriesgaba el flujo de teclado del "Modo Excel" (Enter→fila siguiente + crear-cuenta-nueva).

### UX-04 · Sugerencias Proactivas con "Fuzzy Matching"
- **Descripción:** Actualmente el sistema de sugerencias requiere coincidencias exactas. Implementar lógica para ignorar números al final de las descripciones.
- **Estado:** PENDIENTE — Prioridad MEDIA

### FIX-C · Soporte de PDF consolidado de VEP (ARCA - Seti - Consulta VEP)
- **Reportado por:** Seba Presman (charla 2026-06-07)
- **Descripción:** AFIP/ARCA ahora permite descargar todos los VEP en un único PDF tabular (`ARCA - Seti - Consulta VEP`), columnas `Estado | Enviado a | Nro. VEP | CUIT | Importe | Descripción | Fecha de Pago`. El parser rendía 1 presentación por PDF.
- **Fix:** `PdfAfipParserService` detecta el formato consolidado y rinde N presentaciones. Solo procesa filas `Pagado` (las Expirado/Pendiente no traen fecha y se excluyen solas). Mapea los códigos vía `TaxNameMap` (`SIJPDJ`→Cargas Sociales, `IVA DJ`→IVA A Pagar, `CM-SOP`→Pago IIBB, `HEF-RF`→Honorarios Fiscales, `VCON`→VEP Consolidado) y **descarta los `ARCA##/##` y `AFIP##/##` sin detalle** (acuerdo con Seba). Importe en formato US, regex anclado en CUIT/fecha (PdfPig pega las celdas sin espacios). Test con el PDF real: 80 filas Pagado → 65 mapeadas, 15 descartadas.
- **Estado:** ✅ Completado — 2026-06-07 (rama `dev`)

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

## 🎯 PRIORIDADES ACTIVAS (curadas por Agustín — 2026-06-08)

**Cadena comercial (orden con dependencias):**
1. **COST-01 — Modelar costo unitario por cliente** — ✅ Documentado en [COST-01-unit-economics.md](COST-01-unit-economics.md). Hallazgo: costo marginal ≈ $0 (OCR Tesseract local, sin IA paga); hoy Render+Neon en Free ($0) pero el primer cliente pago obliga el salto a ~$65–85/mes fijo. Break-even ~6 clientes con básico $15.
2. **ENTRY-01 — Modelo de entrada (reframe de QUOTA-01).** ✅ Definido en [ENTRY-01-modelo-de-entrada.md](ENTRY-01-modelo-de-entrada.md): estrategia escalonada **A (sales-led, ahora) → B (trial self-serve) → C (MercadoPago)**. **Fase A operativa**: el registro público ahora rutea a `Pending` (signup llama a `/register`, copy "Solicitá tu prueba"), el admin activa a Pro. Pendiente Fase B: trial self-serve con `TrialEndsAt`.
3. **P1-3 — Landing comercial** (Astro/Next). La landing es el **puente a la adquisición del servicio** (embudo a pago), no solo SEO. Necesita precio + oferta + modelo de entrada definidos para tener un CTA real.

**En paralelo / aparte:**
- **UX-04 — Fuzzy Matching en sugerencias** (ignorar números al final de la descripción). Producto, independiente, win rápido.
- **COST-02 — Migrar EPPlus → ClosedXML.** EPPlus está en `LicenseContext.NonCommercial` pero el producto es comercial (incumplimiento + ~US$599/año si se regulariza). ClosedXML es MIT (gratis). Salió de COST-01.
- **RETEN-01 — Retención/limpieza de datos** (alertas de antigüedad + backup + purga). Post-clientes, no urge sin volumen.

---

## ⏳ TAREAS PENDIENTES (otras)

- Ver "Prioridades activas" arriba.

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