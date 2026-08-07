# ContableAI v1.1 — Plan de Implementación (REVISADO)

> Reemplaza a `PLAN-V1.1.md`. Incorpora las decisiones de negocio del 2026-08-06.
> Rutas relativas a la raíz del repo (`backend/`, `frontend/`).

---

## 0. Decisiones tomadas (cerradas)

| # | Decisión | Impacto en el plan |
|---|----------|--------------------|
| 1 | **Transferencias internas → Cuenta Puente**, no matching en backend. Los asientos se generan **por cuenta bancaria**; una transferencia Galicia→MP genera dos asientos, ambos contra "Valores en Tránsito". | Se elimina el bloqueante más grande. Backend sin lógica de cruce. Pasa a ser tarea de **seed + reglas**. |
| 2 | **Cuenta provisional** al importar (sin doble OCR). | Confirma §1.4. |
| 3 | **`AfipComboMatch` excluido** del override retroactivo. | Confirma §4.3. |
| 4 | Override retroactivo **solo para reglas de empresa**. | Se mantiene el 400 actual (`RulesEndpoints.cs:110-111`). Epic D se achica. |
| 5 | **"Copiar reglas" descartado.** Solo **"Promover a Regla de Estudio"**. | Epic E se achica de 2 días a ~1. Cambia por completo el diseño técnico (ver §5). |
| 6 | **Sin modal** de nombre de archivo. Autogenerado en backend. | Epic A queda en ½ día. |

**Nuevo total estimado: ~4 semanas** (antes ~5). El grueso sigue siendo Feature 1.

---

## 1. La Cuenta Puente cambia menos de lo que parece (y valida el diseño)

### 1.1 Tu corrección confirma la arquitectura propuesta

"Los asientos se generan de forma separada por cada cuenta bancaria" **ya es lo que hace el motor**: `GenerateJournalEntriesCommandHandler` emite **un `JournalEntry` por `BankTransaction`** (`:311-319`), y cada movimiento pertenece a una sola cuenta. Lo único que cambia con F1 es de dónde sale la contrapartida:

```
HOY:    contrapartida = f(moneda)          → Company.BankAccountName | UsdBankAccountName
v1.1:   contrapartida = f(cuenta bancaria) → BankAccount.ContraAccountName
```

Es un cambio de **una función de resolución** (`TryResolveBankAccount`, `:330-335`), no del modelo de asientos. Nada de consolidación cross-cuenta en la generación.

### 1.2 El mecanismo de Cuenta Puente, en asientos

Transferencia de $100.000 de Galicia a Mercado Pago:

| Extracto | Movimiento | Asiento generado |
|----------|-----------|------------------|
| Galicia | Débito $100.000 "TRANSF MISMA TITULARIDAD" | **Debe** Valores en Tránsito 100.000 / **Haber** Banco Galicia 100.000 |
| Mercado Pago | Crédito $100.000 "TRANSF MISMA TITULARIDAD" | **Debe** Banco MP 100.000 / **Haber** Valores en Tránsito 100.000 |

"Valores en Tránsito" queda con Debe 100.000 y Haber 100.000 → **neto cero**, y ni Proveedores ni Cuentas a Cobrar se inflan. Exactamente como lo planteaste.

### 1.3 Hallazgo: esto ya está medio implementado, y mal

`GlobalRules.cs:70` ya tiene la regla:

```csharp
new() { Keyword = "MISMA TITULARIDAD", Direction = null, TargetAccount = "CAJA Y BANCOS", Priority = 12 },
```

Funcionalmente hace de puente (debita de un lado, acredita del otro, netea). **Pero apunta a `"CAJA Y BANCOS"`**, que en `GetDefaultAccounts()` (`GlobalRules.cs:110`) es la cuenta genérica de disponibilidades — la misma que se usa para otras cosas. Consecuencias:

- El neteo se **contamina**: si algún otro movimiento cae en "CAJA Y BANCOS", el saldo deja de ser un indicador limpio.
- El contador no puede usar "CAJA Y BANCOS ≠ 0" como control de transferencias pendientes.

**Acción concreta (barata, alto valor):**
1. Sembrar `"VALORES EN TRANSITO"` en `GlobalRules.GetDefaultAccounts()`.
2. Repuntar la regla `"MISMA TITULARIDAD"` a esa cuenta.
3. Agregar keywords que hoy faltan y son comunes en AR: `"CTA PROPIA"`, `"TRANSFERENCIA ENTRE CUENTAS PROPIAS"`, `"TRASPASO"`, `"MISMO TITULAR"`.
4. Migración de datos: **no** repuntar retroactivamente los movimientos existentes en "CAJA Y BANCOS" — no hay forma de distinguir cuáles eran transferencias. Se deja para que el contador lo resuelva con el override retroactivo (F4), que es justamente para esto.

> **Nota de diseño**: el punto 4 es un caso de uso real y directo de F4. Un contador que hoy tiene 3 años de transferencias en "CAJA Y BANCOS" crea la regla `MISMA TITULARIDAD → VALORES EN TRANSITO`, marca "aplicar a históricos", y el override pisa todo lo no asentado. **Esto refuerza el orden de ejecución: F4 antes que F1.**

### 1.4 La Cuenta Puente es *el* argumento de Feature 3

Con multi-cuenta, "Valores en Tránsito" es **la cuenta que garantizadamente aparece en Debe y en Haber** dentro del mismo período. Si el Formulario de Asiento la netea a una sola línea de $0, el contador pierde la información de que hubo $2.400.000 de transferencias movidas.

Con el split de F3 ve:

```
Valores en Tránsito (Debe)     2.400.000
Valores en Tránsito (Haber)    2.400.000
```

y de un vistazo confirma que cerró. **Si quedara asimétrico, es la señal de que falta subir un extracto.** F3 deja de ser cosmética: es el control de integridad del multi-cuenta.

### 1.5 Consecuencia: F3 sube de prioridad

En el plan anterior F3 era un quick win aislado. Ahora es **prerequisito funcional de F1**: sin split, el diagnóstico de transferencias descuadradas es invisible. Se mantiene en Sprint 1, pero como bloqueante, no como "nice to have".

---

## 2. Feature 1 — Multi-cuenta bancaria (sin cambios estructurales)

El diseño de `PLAN-V1.1.md` §1 se mantiene íntegro. Resumen de lo que sigue vigente:

### 2.1 Modelo

Entidad nueva **`BankAccount`**: `Id`, `CompanyId`, `Alias`, `AccountNumber`, `NormalizedNumber`, `Cbu`, `BankCode`, `Currency`, `ContraAccountName`, `ChartOfAccountId?`, `IsActive`, `StudioTenantId`.

- `BankTransaction + Guid? BankAccountId` (nullable, por huérfanas legacy)
- `JournalEntry + Guid? BankAccountId` (desnormalizado — `JournalEntry` no tiene navegación a `BankTransaction`, mismo argumento que `Currency` en `JournalEntry.cs:17-22`)
- `Company.BankAccountName` / `UsdBankAccountName` → deprecadas, borrar en v1.2

Índices: `IX_BankAccounts_CompanyId_NormalizedNumber` (UNIQUE), `IX_BankTransactions_BankAccountId_Date`, `IX_JournalEntries_CompanyId_BankAccountId`.

### 2.2 Contrato de parsers

`IBankParserService.Parse` pasa de `IEnumerable<BankTransaction>` a `ParsedStatement(Transactions, Currency, Bank, DetectedAccountNumber?, DetectedCbu?)`. Detección bank-agnóstica sobre `StatementLine`, replicando el patrón de `PdfBankParser.DetectCurrency` (`:179-211`) — **no toca ninguna de las 6 estrategias por banco**.

### 2.3 Riesgo crítico que se mantiene

**`TransactionSignatureBuilder.Build` (`:20-23`) no incluye la cuenta bancaria.** Con multi-cuenta, el mismo importe/fecha/descripción en dos cuentas de la misma empresa se descarta como duplicado. **Va en la misma migración o hay pérdida silenciosa de datos.**

Y con la Cuenta Puente esto se agrava: una transferencia interna produce, por definición, **dos movimientos de idéntico importe y fecha con descripción casi igual** en dos cuentas de la misma empresa. Es el caso que **más** va a disparar el falso positivo. Sin el fix de la firma, el multi-cuenta rompe justo el flujo que acabás de definir como central.

### 2.4 Enrutamiento (decisión #2 aplicada)

1. Cuenta explícita del usuario en la Dropzone → gana siempre.
2. OCR detecta número → matchea 1 sola `BankAccount` → enruta.
3. Detecta número desconocido → **crea `BankAccount` provisional** (`ContraAccountName = ""`), importa, y la devuelve en el payload del polling. El asentado queda bloqueado por el 422 que ya existe (`JournalEntriesEndpoints.cs:58-68`) hasta completar la contrapartida.
4. No detecta / ambiguo (2+ matches) → error por archivo vía `parseErrors` (`UploadBankStatementHandler.cs:206`), canal que la UI ya muestra.

### 2.5 Edge cases vigentes

- **Huérfanas legacy** → `BankAccountId` nullable + backfill por (empresa, moneda); bucket "Sin cuenta asignada" con acción bulk.
- **OCR ilegible** → cascada de §2.4. Nunca adivinar aunque la empresa tenga una sola cuenta.
- **PDF con dos cuentas de la misma moneda** → detectar N números distintos y rechazar, igual que hoy con `MixedCurrencyError` (`PdfBankParser.cs:160`).
- **Mismo CBU en dos empresas del estudio** → el enrutamiento automático **exige empresa seleccionada**. No se cruza entre empresas en un mismo lote.
- **OCR y dígitos ambiguos (0/O, 8/B)** → normalizar a dígitos, matchear por últimos 6-8, exigir match único.
- **Cambiar contrapartida con asientos ya emitidos** → advertir ("hay N asientos con la cuenta anterior").
- **Baja de cuenta** → soft-delete obligatorio. `StudioTenantPurger` debe incluir `BankAccounts`.

---

## 3. Feature 3 — Split Debe/Haber (ahora bloqueante)

Sin cambios técnicos respecto del plan anterior. Recordatorio del alcance real:

- La lógica **ya existe** condicionada a `balanceSet`: `ExcelExportService.cs:253-276` y `journal-page.ts:138-150`.
- El cambio es quitar el condicional: clave de agrupación siempre `(cuenta, isDebit)`.
- Se **borra** el parámetro `balanceAccounts` de `IExportService` (`ExcelExportService.cs:13`) y su cálculo en `JournalEntriesEndpoints.cs:310-314` y `:422-426`.
- Se **borran** los hacks `.Replace(" (Debe)", "")` (`ExcelExportService.cs:273-284`) y el regex de `journal-page.ts:175-176`.
- Sufijo `(Debe)`/`(Haber)` **solo cuando la cuenta aparece en ambos lados**; si no, ruido innecesario.

**Riesgo**: la consolidación está duplicada backend/frontend. Los dos cambios van en **el mismo PR**, con test de paridad UI ↔ Excel sobre un fixture con cuenta en ambos lados (usar precisamente un caso de Valores en Tránsito).

---

## 4. Feature 4 — Override retroactivo (alcance reducido)

**Decisión #4 aplicada: solo reglas de empresa.** Se mantiene el rechazo actual de reglas globales/estudio (`RulesEndpoints.cs:110-111`). Esto simplifica el epic: no hay que resolver "reaplicar una regla de estudio sobre N empresas".

### 4.1 Cambio central

En `RulesEndpoints.cs:128-140`, borrar el tercer bloque del filtro. Queda:

```csharp
t.JournalEntryId == null                          // "no asentado" — ya existe, no hace falta estado nuevo
&& t.ClassificationSource != AfipComboMatch       // decisión #3
&& MatchesKeyword(t.Description, rule.Keyword)
&& (rule.Direction == null || t.Type == rule.Direction)
```

### 4.2 Los tres problemas que hay que resolver igual

**(a) Matching inconsistente.** `reapply` usa `t.Description.Contains(rule.Keyword)` → case-sensitive en Postgres y substring exacto. El motor (`HardRuleStrategy.cs:21-31`) hace matching palabra por palabra ordinal-ignore-case tolerando tokens intermedios. Hoy reapply toca **menos** movimientos de los que la regla realmente clasifica; al volverse destructivo, esa asimetría se vuelve inexplicable para el usuario. Unificar con `DescriptionMatchesKeyword` (filtrado en memoria por lotes, o `ILike` + `f_unaccent` que ya está mapeado en `ContableAIDbContext.cs:280-282`).

**(b) `AfipComboMatch`** — excluido por decisión #3. Razón técnica: tiene `AfipVoucher`s vinculados por `MatchedTransactionId` y `ProjectLines` (`:284-301`) desglosa el asiento en base a eso; pisarlo rompe el desglose y huerfaniza los VEPs.

**(c) Períodos cerrados.** Un movimiento en período cerrado sin asiento hoy quedaría reasignado. `ClosedPeriod` ya se valida en generación (`:136-156`) y borrado (`JournalEntriesEndpoints.cs:166`). Respetarlo por consistencia.

### 4.3 UX obligatoria (destruye trabajo manual)

1. Respuesta con desglose: `{ pending, byGlobalRule, manual, skippedSettled, skippedClosedPeriod, skippedAfipCombo }`.
2. Modo `dryRun=true` para previsualizar.
3. En `rules-page.ts:318`, si `manual > 0` → confirmación explícita: *"Se van a sobrescribir 47 movimientos asignados manualmente. No se puede deshacer."*
4. Registrar en `AuditLog` (el `AuditInterceptor` ya existe).

### 4.4 Performance

`ToListAsync()` + `Assign()` uno por uno sobre años de historia = decenas de miles de entidades trackeadas. Batchear de a 500 (mismo patrón que `BatchSize` en el generador) y, sobre un umbral, encolar en Hangfire con polling — infra ya disponible (`JobsEndpoints.cs`, `getJobStatus` en `journal-entry.service.ts:57`).

### 4.5 Caso de uso estrella

La migración de "CAJA Y BANCOS" → "VALORES EN TRANSITO" descrita en §1.3.4. Vale la pena documentarlo en el release note como el ejemplo canónico de la feature.

---

## 5. Feature 5 — Promover a Regla de Estudio (rediseñada)

**Decisión #5: "Copiar" descartado.** Solo promoción de scope. Esto cambia el diseño de raíz — ya no es un endpoint de creación masiva, es una **mutación de scope de una fila existente**.

### 5.1 Semántica

```
Regla de Empresa                    Regla de Estudio
CompanyId = <guid>          →       CompanyId = null
StudioTenantId = null               StudioTenantId = <guid del estudio>
```

Precedencia (`HardRuleStrategy.cs:37-57`): **Empresa > Estudio > Sistema**.

### 5.2 Tres hallazgos técnicos

**(a) `CompanyId` y `StudioTenantId` son `init`** (`AccountingRule.cs:33` y `:40`) — no se pueden mutar desde C#. Hay dos salidas:

- `ExecuteUpdateAsync` (opera a nivel SQL, saltea `init`) — **es la correcta**, y el patrón ya se usa en `RulesEndpoints.cs:35-43`.
- Delete + recreate — **descartar**: cambiaría el `Id`, y `BankTransaction.AppliedRuleId` (`BankTransaction.cs:29`) quedaría apuntando a una regla inexistente en todos los movimientos ya clasificados. `ExecuteUpdate` **preserva el `Id`** y por lo tanto toda la trazabilidad histórica. Es el argumento decisivo.

**(b) Desajuste de tipos en `StudioTenantId`, con riesgo real.**
`Company.StudioTenantId` es `string` (`Company.cs:34`); `AccountingRule.StudioTenantId` es `Guid?` (`AccountingRule.cs:40`). El upload handler ya convive con esto vía `Guid.TryParse` (`UploadBankStatementHandler.cs:281`).

**El riesgo:** el valor legacy `"ESTUDIO_DEFAULT"` **no parsea como Guid**. Si el endpoint de promoción hace `Guid.TryParse(...)` sin validar y usa el `out` fallido, el resultado es `StudioTenantId = null` → y `CompanyId = null` + `StudioTenantId = null` es, por definición (`AccountingRule.cs:36-38`), una **regla de sistema que aplica a TODOS los estudios de la plataforma**. Un contador promoviendo una regla de su cliente la publicaría a todos los tenants.

**Guarda obligatoria:** rechazar la promoción con 422 si `tenant.StudioTenantId` no parsea como `Guid`. Test explícito para este caso.

**(c) Inversión de precedencia.** La regla promovida **baja de tier**: pasa de ganarle a todo (Empresa) a ceder ante cualquier regla de empresa. Si la empresa origen tiene otra regla con keyword solapado, la promovida puede dejar de aplicar **en su propia empresa de origen**. Hay que detectarlo y avisar.

### 5.3 Diseño del endpoint

```
POST /api/rules/{id}/promote-to-studio
Response 200: { ruleId, affectedCompanies: N, conflicts: [{ companyId, companyName, existingRuleKeyword }] }
Response 422: tenant no promocionable (ESTUDIO_DEFAULT) | la regla ya es de estudio
Response 404: regla inexistente o de otro estudio
```

Con `dryRun=true` para el preview. Validaciones:
- La regla existe y es de **empresa** (`CompanyId != null`).
- La empresa pertenece al estudio del usuario autenticado.
- `tenant.StudioTenantId` parsea como `Guid` (guarda de §5.2b).
- Detectar reglas de empresa con keyword solapado en **otras** empresas del estudio (reusar el criterio `keywordsOverlap` + `directionsCompatible` de `rules-page.ts:304-316`, llevado al server).

### 5.4 Hallazgo de seguridad (se mantiene, sigue siendo prerequisito)

**`AccountingRule` no tiene Global Query Filter.** `ContableAIDbContext.cs:315-324` solo cubre `Company` y `BankTransaction`. Los endpoints `PUT /api/rules/{id}`, `DELETE /api/rules/{id}` y los dos PATCH (`RulesEndpoints.cs:20-99`) buscan solo por `Id`, sin validar estudio → **IDOR cross-tenant preexistente**.

Un endpoint de promoción que muta el scope de una regla amplifica esto: sin el filtro, un `StudioOwner` podría promover a **su propio estudio** una regla de otro estudio, apropiándose de ella. **Cerrarlo antes de Epic E**, con `HasQueryFilter` sobre `AccountingRule` o desnormalizando `StudioTenantId` en todas las reglas (más consistente con P-2).

### 5.5 UI

- Acción "Promover a regla de estudio" en el menú de fila de `rules-table`.
- Modal de confirmación con el preview: *"Esta regla pasará a aplicar a las 12 empresas del estudio. 2 empresas ya tienen una regla propia con keyword similar y seguirán usando la suya."*
- Tras promover, la fila se mueve de la pestaña "Propias" a "Estudio" (`RuleFilterType` en `rules.types.ts:3` ya distingue `own` / `global`).

### 5.6 Interacción con F4 — orden importa

F4 aplica **solo a reglas de empresa** (decisión #4). Una vez promovida, la regla ya no es reaplicable retroactivamente desde la UI. **Secuencia correcta para el usuario: crear → aplicar a históricos → promover.** Hay que decirlo en el copy del modal, si no el usuario promueve primero y después no entiende por qué no puede reaplicar.

---

## 6. Feature 2 — Nombre de archivo (sin modal)

**Decisión #6 aplicada.** Fix de 3 líneas:

1. `+.WithExposedHeaders("Content-Disposition")` en la policy CORS (`ServiceExtensions.cs:43-50`). Sin esto el navegador no puede leer el header, aunque el backend ya lo mande.
2. En `journal-entry.service.ts` (4 métodos: `downloadExcel:113`, `_downloadBlob:168`) y `transaction.ts:218`: `observe: 'response'`, parsear `filename` del header, fallback al nombre actual.
3. El backend ya genera `LibroDiario_{Empresa}_{MM-YYYY}.xlsx` (`JournalEntriesEndpoints.cs:326` y `:438`). Con F1, agregarle la cuenta bancaria cuando el filtro esté activo.

---

## 7. Plan de ejecución revisado

### Sprint 1 — Fundaciones (4 días)

| Epic | Contenido | Días |
|------|-----------|------|
| **A** — Export UX | CORS `WithExposedHeaders` + leer header en 3 services | ½ |
| **B** — Split Debe/Haber | Generalizar agrupación en `journal-page.ts` + `ExcelExportService`; borrar `balanceAccounts`; test de paridad | 2 |
| **C** — Cuenta Puente | Sembrar `VALORES EN TRANSITO`; repuntar `MISMA TITULARIDAD`; agregar keywords AR | ½ |
| **D** — Hardening de reglas | Global Query Filter sobre `AccountingRule` (cierra IDOR §5.4); unificar matching de keyword | 1 |

> A, B y C son independientes → paralelizables. D es prerequisito de los sprints 2.

### Sprint 2 — Reglas (3 días)

| Epic | Contenido | Días |
|------|-----------|------|
| **E** — Override retroactivo (F4) | Nuevo filtro; exclusiones (`AfipComboMatch`, períodos cerrados); `dryRun` + desglose; confirmación destructiva; `AuditLog`; batching | 2 |
| **F** — Promover a estudio (F5) | `POST /rules/{id}/promote-to-studio` con `ExecuteUpdate`; guarda de `Guid.TryParse`; detección de conflictos; modal | 1 |

### Sprints 3-5 — Multi-cuenta (F1) · 2-3 semanas

Cinco fases, cada una desplegable por separado:

| Fase | Contenido | Días | Nota |
|------|-----------|------|------|
| **1.a** | Entidad `BankAccount`, `DbContext`, índices, migración + backfill | 3 | **Deploy sin cambio de comportamiento** — valida el backfill en prod con riesgo cero |
| **1.b** | CRUD + pestaña "Cuentas bancarias" en `company-modal` | 3 | Los strings viejos siguen siendo fuente de verdad |
| **1.c** | `TryResolveBankAccount` por cuenta; 422 por cuenta | 3 | **Punto de no retorno**: `Company.BankAccountName` deja de usarse |
| **1.d** | `ParsedStatement`, detección OCR, `BankAccountId` en la firma de dedup, enrutamiento, cuentas provisionales | 5 | **La más riesgosa** — ver gate abajo |
| **1.e** | Query params, `AvailableBankAccounts`, filtros y columna dinámica en ambas grillas, export por cuenta | 4 | |

**Gate de calidad para 1.d:** medir tasa de detección de número de cuenta por banco contra los fixtures reales de `tests/extractos/` (BBVA, Galicia, Credicoop, Ciudad, MercadoPago, y los casos de falla conocidos en `BBVA FALLAS 15-7-2026` y `CREDICOOP - ERROR`). **Banco por debajo de ~90% arranca en modo manual obligatorio**, no en auto.

---

## 8. Riesgos vivos

| Riesgo | Severidad | Mitigación |
|--------|-----------|------------|
| Firma de dedup sin `BankAccountId` → pérdida silenciosa de movimientos, agravada por las transferencias internas (§2.3) | **Alta** | Va en la migración de 1.a, con test específico de dos cuentas con el mismo importe/fecha |
| Promoción con `ESTUDIO_DEFAULT` → regla filtrada a todos los tenants (§5.2b) | **Alta** | Guarda de `Guid.TryParse` + test |
| IDOR en endpoints de reglas (§5.4) | **Alta** | Epic D, prerequisito de Sprint 2 |
| Consolidación duplicada backend/frontend → UI y Excel divergen (§3) | Media | Mismo PR + test de paridad |
| Detección de cuenta por OCR insuficiente en algún banco | Media | Gate de 1.d: fallback a modo manual por banco |
| Override retroactivo destruye trabajo manual | Media | `dryRun` + confirmación + `AuditLog` |
| Inversión de precedencia al promover (§5.2c) | Baja | Preview de conflictos en el modal |

---

## 9. Fuera de alcance v1.1 (registrado para v1.2)

- Reaplicar retroactivamente reglas de estudio/sistema (decisión #4).
- Copiar reglas entre empresas (decisión #5) — reevaluar si "Promover" no cubre el caso real.
- Borrar `Company.BankAccountName` / `UsdBankAccountName` y sus commands.
- Migrar `JournalEntryLine.Account` de string a FK a `ChartOfAccount`.
- Unificar el tipo de `StudioTenantId` (`string` en `Company` vs `Guid?` en `AccountingRule` / `ChartOfAccount`).
- Detección automática de transferencias internas sin regla (matching por importe/fecha entre cuentas) — **explícitamente descartado para v1.1**: la Cuenta Puente lo resuelve sin complejidad en backend.
