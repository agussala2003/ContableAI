# ContableAI — Backlog & Roadmap Unificado

> **Documento único de trabajo.** Combina feedback de cliente, bugs reportados, mejoras de UX, features planificadas y roadmap estratégico.
> Última actualización: 2026-05-08 (Sesión: Auditoría profunda pre-producción, Fix Vercel/Render, Git Flow)

## 🏗️ ESTADO DEL PRODUCTO (Baseline — Q1 2026)

**Stack:** `.NET 10` + `Angular 21` + `PostgreSQL` (Clean Architecture)

**Funcionalidades activas en producción:**
- Parsers multi-banco:
  - **PDF** (`IBankStatementParser`, la vía real de carga): BBVA, Galicia, **Santander** ✨, Credicoop, Banco Ciudad, MercadoPago, genérico tabular.
  - **CSV / XLSX** (`IBankParser`): BBVA, Galicia, Santander, Macro, Nación, MercadoPago, Ualá, Credicoop.
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
- **Descripción:** Las sugerencias no agrupaban descripciones que solo diferían en los números finales (ej: `FACTURA0012` vs `FACTURA0034` caían al fallback de la descripción completa y no agrupaban).
- **Fix:** Nuevo helper único `KeywordNormalizer.Normalize` (Domain) que quita los **dígitos al final** de cada token (`FACTURA0012` → `FACTURA`) y descarta tokens con dígitos internos/vacíos. Centraliza las 3 copias previas de `NormalizeKeyword` (ProactiveLearningService, CompanyEndpoints, TransactionEndpoints). +7 tests.
- **Estado:** ✅ Completado — 2026-06-08

### FIX-C · Soporte de PDF consolidado de VEP (ARCA - Seti - Consulta VEP)
- **Reportado por:** Seba Presman (charla 2026-06-07)
- **Descripción:** AFIP/ARCA ahora permite descargar todos los VEP en un único PDF tabular (`ARCA - Seti - Consulta VEP`), columnas `Estado | Enviado a | Nro. VEP | CUIT | Importe | Descripción | Fecha de Pago`. El parser rendía 1 presentación por PDF.
- **Fix:** `PdfAfipParserService` detecta el formato consolidado y rinde N presentaciones. Solo procesa filas `Pagado` (las Expirado/Pendiente no traen fecha y se excluyen solas). Mapea los códigos vía `TaxNameMap` (`SIJPDJ`→Cargas Sociales, `IVA DJ`→IVA A Pagar, `CM-SOP`→Pago IIBB, `HEF-RF`→Honorarios Fiscales, `VCON`→VEP Consolidado) y **descarta los `ARCA##/##` y `AFIP##/##` sin detalle** (acuerdo con Seba). Importe en formato US, regex anclado en CUIT/fecha (PdfPig pega las celdas sin espacios). Test con el PDF real: 80 filas Pagado → 65 mapeadas, 15 descartadas.
- **Estado:** ✅ Completado — 2026-06-07 (rama `dev`)

---

## 🚀 FEATURES NUEVAS Y CONFIGURACIÓN

### PARSER-SANTANDER · Extractos PDF de Banco Santander
- **Reportado por:** Cliente (3 bancos en una misma empresa: Santander, Galicia y MercadoPago).
- **Corrección de este documento:** el baseline afirmaba que Santander ya estaba soportado. Era **falso para PDF**, que es la vía real de carga. Existía `SantanderParser` para CSV/XLSX, pero `BankParserFactory` enruta todo `.pdf` a `PdfBankParser`, cuyo despacho es por `IBankStatementParser` — y ahí Santander no estaba. Sus extractos caían en `GENERIC` y los interpretaba el motor tabular sin conocer el formato.
- **Fix:** Nuevo `SantanderStatementParser` (`IBankStatementParser`), registrado en `PdfBankParser`. Detección del banco en `OcrStatementExtractor` por nombre de archivo y por prefijo 072 del CBU — el logo del encabezado es vectorial, así que la palabra "Santander" no está en el texto extraíble. Ruta digital (PdfPig), sin OCR. Particularidades cubiertas: símbolo de moneda en celda propia, saldo negativo con el menos pegado al símbolo (`-$`), fila "Saldo Inicial" con fecha pero sin movimiento, descripciones en dos renglones, y exclusión de las filas "Total" / "Saldo total" y del anexo "Detalle impositivo".
- **Resúmenes consolidados:** 2 de los 11 extractos de muestra apilan **dos cuentas** en un mismo PDF. La guarda compartida (`DetectAccountIdentifiers`) no los veía porque solo mira las primeras 40 líneas y el segundo bloque arranca más abajo. El parser de Santander los rechaza con el mensaje al usuario ya existente, en vez de mezclar los movimientos de las dos cuentas. **Pendiente:** evaluar si conviene ampliar la ventana de la guarda compartida — requiere revalidar el corpus de los otros bancos.
- **Tests:** `SantanderParserTests` — 7 sobre fixture sintético versionado (corren siempre, también en CI) y 4 de regresión sobre los 11 PDFs reales. La aserción fuerte es la **cadena de saldos**: cada saldo debe ser el anterior ± el importe, y el cierre de cada mes debe ser la apertura del siguiente. Verificado sobre 9 extractos consecutivos (09.25 → 05.26), 565 movimientos, sin una sola rotura.
- **Estado:** ✅ Completado — 2026-08-27

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
3. **P1-3 — Landing comercial.** 🔶 **Construida** como ruta pública `/inicio` dentro del Angular (no Astro: reusa Tailwind/Lucide/Vercel y el flujo "Solicitá tu prueba"). Hero + features + cómo funciona + pricing (**Pro US$20** / **Enterprise a medida**) + CTA → `/login?register=1`. `authGuard` redirige no-logueados a `/inicio`. **Pendiente:** deploy a `main` + (opcional) contacto real para Enterprise + revisar copy con Seba.

**En paralelo / aparte:**
- **UX-04 — Fuzzy Matching en sugerencias** (ignorar números al final de la descripción). Producto, independiente, win rápido.
- **COST-02 — Migrar EPPlus → ClosedXML.** ✅ Completado — 2026-06-08. Saca el riesgo legal de licencia (EPPlus NonCommercial en producto comercial) y el costo (~US$599/año). Migrados escritura (`ExcelExportService`) y lectura (`BankParserFactory`, `CsvBankParserService`, `ChartOfAccountsEndpoints`). Sin samples reales → +3 tests round-trip sintéticos (escritura legible + parsers Galicia/BBVA). EPPlus removido del csproj.
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