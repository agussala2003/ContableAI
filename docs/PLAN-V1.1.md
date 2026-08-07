# ContableAI v1.1 — Análisis de Impacto y Plan de Implementación

> Estado: **propuesta para aprobación**. Nada de esto está implementado todavía.
> Todas las rutas son relativas a la raíz del repo (`backend/`, `frontend/`).

---

## 0. Resumen ejecutivo

| # | Feature | Esfuerzo | Riesgo | Migración DB | Bloquea a |
|---|---------|----------|--------|--------------|-----------|
| 2 | Nombre de archivo en export Excel | XS (½ día) | Bajo | No | — |
| 3 | Split Debe/Haber en consolidado | S (1-2 días) | **Medio** (contable) | No | Vistas consolidadas de F1 |
| 5 | Portabilidad de reglas entre empresas | S (2 días) | Bajo | No | — |
| 4 | Override retroactivo de reglas | M (2-3 días) | **Alto** (destructivo) | No | — |
| 1 | Multi-cuenta bancaria + auto-enrutamiento | **XL (2-3 semanas)** | **Alto** | Sí (+backfill) | Todo lo demás |

**Hallazgos que cambian el alcance respecto de lo que pediste:**

1. **Feature 2 ya está hecha en el backend.** `JournalEntriesEndpoints.cs:326` y `:438` ya devuelven `LibroDiario_{Empresa}_{MM-YYYY}.xlsx`. El frontend lo **descarta** y hardcodea el nombre (`journal-entry.service.ts:120`). Además falta `WithExposedHeaders("Content-Disposition")` en CORS, así que hoy el navegador ni siquiera puede leer el nombre que manda el servidor. Es un fix de 3 líneas, no una feature.

2. **Feature 3 ya existe, pero solo para las cuentas bancarias.** `ExcelExportService.cs:253-276` ya separa Debe/Haber vía `balanceSet`, y `journal-page.ts:133-183` hace lo mismo en la UI. La feature es *generalizar* esa lógica a todas las cuentas y **borrar** el concepto `balanceAccounts`, no escribirla de cero.

3. **Feature 1 rompe el contrato de los parsers.** `IBankParserService.Parse` (`CsvBankParserService.cs:16`) e `IBankStatementParser.Parse` (`IBankStatementParser.cs:22`) devuelven `IEnumerable<BankTransaction>`: **no hay canal para metadata a nivel documento**. Hoy la moneda se resuelve con un post-pass en `PdfBankParser.cs:78-80` justamente porque no hay dónde ponerla. El número de cuenta necesita el mismo canal, y esta vez conviene hacerlo bien (ver §1.3).

4. **Riesgo silencioso de F1:** la firma de deduplicación (`TransactionSignatureBuilder.cs:20-23`) **no incluye la cuenta bancaria**. Con multi-cuenta, un mismo importe/fecha/descripción en dos cuentas de la misma empresa se marcará como duplicado y se descartará. Esto hay que arreglarlo *en la misma migración*, si no se pierden movimientos reales sin aviso.

---

## 1. Feature 1 — Multi-cuenta bancaria y auto-enrutamiento

### 1.1 Estado actual

La "cuenta bancaria" hoy son **dos strings sueltos en `Company`**:

- `Company.BankAccountName` (`Company.cs:47`) — contrapartida ARS
- `Company.UsdBankAccountName` (`Company.cs:54`) — contrapartida USD

y la contrapartida se elige **por moneda**, no por cuenta:
`GenerateJournalEntriesCommandHandler.TryResolveBankAccount` (`:330-335`).

No hay número de cuenta en ningún lado. `BankTransaction` no tiene ninguna referencia a una cuenta bancaria; solo `CompanyId` (`BankTransaction.cs:66`).

### 1.2 Modelo de datos propuesto

**Entidad nueva: `BankAccount`**

```
Id                  Guid
CompanyId           Guid          (FK, requerido)
Alias               string        "BBVA CC $ — Operativa"
AccountNumber       string?       número tal cual figura en el extracto
NormalizedNumber    string        solo dígitos, para matching del OCR
Cbu                 string?       opcional, 22 dígitos
BankCode            string?       BBVA | GALICIA | ... (BankCodes)
Currency            string        ARS | USD
ContraAccountName   string        contrapartida contable (el actual BankAccountName)
ChartOfAccountId    Guid?         FK opcional al plan de cuentas (para el código externo)
IsActive            bool
StudioTenantId      string        desnormalizado, igual que BankTransaction
```

**Por qué `ContraAccountName` sigue siendo string y no solo FK:** todo el motor de asientos trabaja con nombres de cuenta como string (`JournalEntryLine.Account`, `AccountingRule.TargetAccount`, `AccountNameResolver`). Migrar eso a FK es un refactor aparte. Se guarda el `ChartOfAccountId` **además**, para resolver el código externo (Tango/Holistor/Bejerman) sin el lookup por nombre que hoy hace `JournalEntriesEndpoints.cs:317-320`.

**Cambios en entidades existentes:**

| Entidad | Cambio | Nota |
|---------|--------|------|
| `BankTransaction` | `+ Guid? BankAccountId` | **Nullable** por las huérfanas legacy |
| `JournalEntry` | `+ Guid? BankAccountId` | **Desnormalizado**, ver abajo |
| `Company` | `BankAccountName` / `UsdBankAccountName` → deprecar | Mantener 1 release, borrar en v1.2 |

> **Por qué desnormalizar `BankAccountId` en `JournalEntry`:** el filtro de la grilla de asientos tiene que poder filtrar sin joinear. `JournalEntry` **no tiene navegación a `BankTransaction`** (`JournalEntry.cs` solo guarda `BankTransactionId` suelto, sin FK navegable) — es exactamente el mismo argumento por el que ya se desnormalizó `Currency` (ver el comentario en `JournalEntry.cs:17-22`). Consistente con la arquitectura existente.

**Índices necesarios:**
- `IX_BankAccounts_CompanyId_NormalizedNumber` (UNIQUE) — enrutamiento y anti-duplicado
- `IX_BankTransactions_BankAccountId_Date`
- `IX_JournalEntries_CompanyId_BankAccountId`

### 1.3 Cambio de contrato en los parsers (el corazón del refactor)

Hoy:
```csharp
IEnumerable<BankTransaction> Parse(Stream, string bankCode, string fileName);
```

Propuesto:
```csharp
ParsedStatement Parse(Stream, string bankCode, string fileName);

record ParsedStatement(
    IReadOnlyList<BankTransaction> Transactions,
    string   Currency,
    string   Bank,
    string?  DetectedAccountNumber,   // ← nuevo
    string?  DetectedCbu);            // ← nuevo
```

- **Detección**: replica el patrón de `PdfBankParser.DetectCurrency` (`:179-211`) — escaneo **bank-agnóstico** sobre `StatementLine` ya extraídas, así funciona igual en ruta digital y OCR. Regex sobre las primeras ~40 filas buscando CBU (22 dígitos), `CC $ NNN-NNNNNN/N`, `3-029-NNNNN`, etc.
- **Ventaja del enfoque**: no toca ninguna de las 6 implementaciones de `IBankStatementParser`. Igual que la moneda hoy.
- **Costo**: `CsvBankParserService` (CSV/XLSX) también implementa `IBankParserService` → devuelve `DetectedAccountNumber = null` siempre. Aceptable: los CSV/XLSX ya no detectan ni moneda.

### 1.4 Enrutamiento en el upload

`UploadBankStatementHandler` hoy resuelve **una** empresa para **todo** el lote (`:172-183`) y carga presupuesto de duplicados, reglas y candidatos de unión **una vez** para esa empresa (`:127-129`). Con N cuentas mezcladas:

- El **presupuesto de duplicados debe ser por (empresa, cuenta bancaria)**, no por empresa. Requiere agregar `BankAccountId` a `TransactionSignatureBuilder.Build` (`TransactionSignatureBuilder.cs:20-23`). **Sin esto se pierden movimientos.**
- La **detección de duplicados "de unión"** (`:392-426`) también se acota por cuenta.
- Las reglas siguen siendo por empresa/estudio → sin cambio.

**Flujo de resolución por archivo:**

1. ¿El usuario eligió una cuenta explícita en la Dropzone? → **gana siempre**, no se enruta.
2. ¿El OCR detectó número y matchea 1 sola `BankAccount` de la empresa? → enruta ahí.
3. ¿Detectó número que no existe? → **crear la cuenta automáticamente en estado provisional** (`ContraAccountName = ""`), importar los movimientos y devolver la cuenta nueva en el payload del polling para que la UI pida completar la contrapartida.
4. ¿No detectó nada o matchea 2+ cuentas (ambiguo)? → error por archivo vía el canal `parseErrors` que **ya existe** (`UploadBankStatementHandler.cs:206`) y que la UI ya sabe mostrar.

> **Recomendación fuerte sobre el punto 3.** El pedido dice "sugerir su creación", lo cual implica un modal **antes** de importar. Pero el OCR corre dentro del job de Hangfire (async, fire-and-forget): para preguntar antes habría que correr el OCR dos veces (una para detectar, otra para importar) — en un PDF escaneado de 40 páginas eso son minutos duplicados de CPU. La creación provisional + prompt posterior da la misma UX percibida a un tercio del costo, y reutiliza el 422 "Cuenta bancaria no configurada" que ya existe (`JournalEntriesEndpoints.cs:58-68`) para bloquear el asentado hasta que se complete. **Decisión tuya**, pero recomiendo la provisional.

### 1.5 Endpoints

| Método | Ruta | Nota |
|--------|------|------|
| GET | `/api/companies/{id}/bank-accounts` | nuevo |
| POST | `/api/companies/{id}/bank-accounts` | nuevo, `RequireStudioOwner` |
| PUT | `/api/bank-accounts/{id}` | nuevo |
| PATCH | `/api/bank-accounts/{id}/deactivate` | nuevo (soft-delete, hay historia colgando) |
| GET | `/api/transactions` | `+bankAccountId` query param, `+AvailableBankAccounts` en la respuesta (junto a `AvailableAccounts`, `TransactionEndpoints.cs:346`) |
| GET | `/api/journal-entries` | `+bankAccountId` |
| GET/POST | `/api/journal-entries/export*` | `+bankAccountId` (4 endpoints: xlsx GET/POST, holistor, bejerman, csv) |
| POST | `/api/journal-entries/generate` | valida contrapartida **por cuenta**, no por moneda |
| POST | `/api/transactions/upload` | `+bankAccountId` opcional (modo explícito) |

### 1.6 Frontend

| Archivo | Cambio |
|---------|--------|
| `core/services/bank-account.service.ts` | **nuevo** — CRUD + signal de cuentas de la empresa activa |
| `reconciliation/models/reconciliation.models.ts:3` | `+bankAccountId: string \| null` en `ReconciliationFilters` |
| `reconciliation/components/transaction-grid/*` | columna condicional + selector en el toolbar. **Ojo con los `colspan`** de las filas de totales/empty state y con `transaction-skeleton` |
| `reconciliation/components/company-modal/*` | pestaña "Cuentas bancarias" (reemplaza los 2 inputs sueltos) |
| `reconciliation/components/upload-zone/*` | selector de cuenta con opción "Detectar automáticamente" |
| `journal/pages/journal-page/*` | filtro + columna condicional; `accountGroups` (`:133`) pasa a agrupar por cuenta bancaria cuando se ve "Todas" |

**Columna dinámica** — con signals es directo:
```ts
showBankAccountColumn = computed(() => this.filters().bankAccountId === null);
```

### 1.7 Migración y backfill

```sql
-- 1. Crear BankAccounts desde los strings actuales
INSERT INTO "BankAccounts" (...)
SELECT gen_random_uuid(), c."Id", c."Name" || ' — ARS', NULL, '', 'ARS',
       c."BankAccountName", true, c."StudioTenantId"
FROM "Companies" c WHERE COALESCE(c."BankAccountName",'') <> '';
-- ídem para UsdBankAccountName con Currency='USD'

-- 2. Backfill de movimientos: por empresa + moneda
UPDATE "BankTransactions" t SET "BankAccountId" = ba."Id"
FROM "BankAccounts" ba
WHERE ba."CompanyId" = t."CompanyId" AND ba."Currency" = t."Currency";

-- 3. Ídem JournalEntries (join por BankTransactionId)
```

Empresas sin `BankAccountName` configurado → sus movimientos quedan con `BankAccountId = NULL`. Es el estado correcto: ya hoy no se pueden asentar.

---

## 2. Feature 2 — Nombre de archivo en el export

**No es una feature, es un bug de 3 líneas.**

- `JournalEntriesEndpoints.cs:326` ya construye `LibroDiario_{Empresa}_{MM-YYYY}.xlsx` y lo manda en `Content-Disposition`.
- `journal-entry.service.ts:120` lo tira y escribe `LibroDiario_${mLabel}-${yLabel}.xlsx`. Ídem `_downloadBlob:175` para Holistor/Bejerman/CSV y `transaction.ts:225` para el export de movimientos.
- **`ServiceExtensions.cs:43-50` no expone `Content-Disposition`.** `AllowAnyHeader()` aplica a los headers de *request*; para que el JS pueda leer un header de *response* hace falta `.WithExposedHeaders("Content-Disposition")`. Sin esto, el fix del frontend no funciona y parece un bug del backend.

**Plan (½ día):**
1. `+.WithExposedHeaders("Content-Disposition")` en la policy CORS.
2. En el service: pedir `observe: 'response'`, parsear el `filename` del header, con fallback al nombre actual si no viene.
3. Incluir la cuenta bancaria en el nombre del lado del servidor una vez que exista F1.

**Sobre el modal para tipear el nombre:** lo desaconsejo como default. Agrega un click a una acción que se repite muchas veces por sesión, y el nombre server-side ya es correcto. Si lo querés igual, que sea un "Exportar como…" secundario en el dropdown, no el flujo principal.

---

## 3. Feature 3 — Split Debe/Haber en el consolidado

### 3.1 Estado actual

La lógica **ya existe**, pero condicionada a `balanceSet` (las cuentas bancarias de las empresas del estudio):

- Backend: `ExcelExportService.cs:253-276` — `key = isBalance ? "{account}__{D|H}" : account`
- Frontend: `journal-page.ts:138-150` — `groupKeyFor` / `groupLabelFor`, con `balanceAccount` = cuenta de la empresa activa

`balanceAccounts` se calcula en `JournalEntriesEndpoints.cs:310-314` y `:422-426` y se pasa al export service.

### 3.2 Cambio

Quitar el condicional: **la clave de agrupación es siempre `(cuenta, isDebit)`**. Con eso:

- Se puede **borrar** el parámetro `balanceAccounts` de `IExportService.ExportJournalEntriesToExcel` (`ExcelExportService.cs:13`) y su cálculo en los dos endpoints.
- Se pueden **borrar** los hacks `.Replace(" (Debe)", "")` de `ExcelExportService.cs:273-284` y el regex `/ \((Debe|Haber)\)$/i` de `journal-page.ts:175-176`, porque el label ya no necesita parsearse de vuelta.

**Refinamiento de UX que recomiendo:** poner el sufijo `(Debe)` / `(Haber)` **solo cuando la cuenta aparece en ambos lados**. Si una cuenta solo tiene Debe, mostrarla como "Sueldos a Pagar" y no "Sueldos a Pagar (Debe)" — si no, el 90% del formulario se llena de sufijos ruidosos. La separación en filas es siempre; el sufijo es condicional.

### 3.3 Lo que NO cambia

- Totales, cuadratura (`isBalanced`, `journal-page.ts:197`) y sumas de Excel: idénticos. Es puro reagrupamiento.
- Holistor / Bejerman / CSV (`ExcelExportService.cs:400-454`): **no consolidan**, emiten línea por línea. Sin impacto.
- `GenerateJournalEntriesCommandHandler.ProjectLines` (`:268`): no toca. El split es de *presentación*, no de generación. Un asiento individual nunca tiene la misma cuenta en ambos lados.

### 3.4 Riesgo

Backend y frontend implementan la consolidación **por separado**. Si se cambia uno solo, la UI y el Excel muestran números distintos para el mismo período — el peor tipo de bug en un producto contable. **Los dos cambios van en el mismo PR, con un test que compare ambas salidas** sobre un fixture con una cuenta en ambos lados.

---

## 4. Feature 4 — Override retroactivo de reglas

### 4.1 Estado actual

`POST /api/rules/{id}/reapply` (`RulesEndpoints.cs:101-172`). El filtro de candidatos (`:128-140`):

```csharp
t.JournalEntryId == null
&& t.Description.Contains(rule.Keyword)
&& (rule.Direction == null || t.Type == rule.Direction)
&& (t.AssignedAccount == null
    || t.ClassificationSource == Pending
    || (t.ClassificationSource == HardRule && globalRuleIds.Contains(t.AppliedRuleId)))
```

Disparado desde `rules-page.ts:318-338` cuando `applyRetroactive()` está en true.

### 4.2 Cambio pedido

Borrar el tercer bloque. `JournalEntryId == null` **ya es** la condición "no asentado" que pediste — no hace falta un estado nuevo.

### 4.3 Tres problemas que hay que resolver junto con esto

**(a) El matching de keyword es inconsistente y más restrictivo que el motor real.**
`reapply` usa `t.Description.Contains(rule.Keyword)` → traducido a SQL, es *case-sensitive* en Postgres y exige substring exacto. El motor de clasificación (`HardRuleStrategy.cs:21-31`) hace matching palabra por palabra, ordinal-ignore-case, tolerando tokens intermedios ("COELSA EMPRESA SA" matchea "COELSA 12345 EMPRESA SA").

Consecuencia: **hoy reapply toca menos movimientos de los que la regla realmente clasificaría.** Al pasar a modo destructivo, esa inconsistencia se vuelve visible y confusa ("¿por qué reasignó estos 12 y no esos otros 30 iguales?"). Hay que unificarlo con `DescriptionMatchesKeyword`, aceptando que el filtrado pasa a hacerse en memoria por lotes (o vía `ILike` + `f_unaccent`, que ya está mapeado en `ContableAIDbContext.cs:280-282`).

**(b) `AfipComboMatch` no se puede pisar.**
Un movimiento con `ClassificationSource = AfipComboMatch` (`ClassificationSources.cs:41`) tiene `AfipVoucher`s vinculados por `MatchedTransactionId`, y `ProjectLines` (`GenerateJournalEntriesCommandHandler.cs:284-301`) genera un desglose por impuesto en base a eso. Si una regla pisa el `ClassificationSource`, el asiento futuro pierde el desglose silenciosamente y los VEPs quedan huérfanos. **Recomiendo excluir `AfipComboMatch` del override**, además de "Asentado".

**(c) Períodos cerrados.**
Un movimiento en un período cerrado sin asiento generado hoy quedaría reasignado. `ClosedPeriod` ya se valida en la generación (`GenerateJournalEntriesCommandHandler.cs:136-156`) y en el borrado (`JournalEntriesEndpoints.cs:166`). Por consistencia, reapply debería respetarlo también.

### 4.4 UX obligatoria

El nuevo comportamiento **destruye trabajo manual del contador** (movimientos con `ClassificationSource = Manual`). Propongo:

1. La respuesta de reapply devuelve un desglose: `{ pending: N, byGlobalRule: N, manual: N, alreadySettled: N (no tocados) }`.
2. Modo `dryRun=true` para previsualizar.
3. En `rules-page.ts:318`, si `manual > 0`, confirmación explícita: *"Se van a sobrescribir 47 movimientos asignados manualmente. Esta acción no se puede deshacer."*
4. Registrar en `AuditLog` (el `AuditInterceptor` ya existe) para poder reconstruir qué pisó qué.

### 4.5 Performance

`ToListAsync()` sobre todos los candidatos y luego `Assign()` uno por uno. Con "todos los movimientos coincidentes" de una empresa con años de historia, esto puede ser decenas de miles de entidades trackeadas. Batchear de a 500 (mismo patrón que `GenerateJournalEntriesCommandHandler.BatchSize`) y, si supera un umbral, **encolar en Hangfire** con polling — la infra ya está (`JobsEndpoints.cs`, `getJobStatus` en `journal-entry.service.ts:57`).

### 4.6 Fuera de alcance a confirmar

`reapply` hoy **rechaza reglas globales y de estudio** (`RulesEndpoints.cs:110-111`). Con F5 en juego (copiar reglas entre empresas) es esperable que el usuario también quiera reaplicar una regla de estudio. ¿Lo incluimos en v1.1?

---

## 5. Feature 5 — Portabilidad de reglas entre empresas

### 5.1 Hallazgo de seguridad previo

**`AccountingRule` no tiene Global Query Filter.** En `ContableAIDbContext.cs:315-324` solo lo tienen `Company` y `BankTransaction`. Los endpoints `PUT /api/rules/{id}`, `DELETE /api/rules/{id}` y los dos PATCH de activar/desactivar (`RulesEndpoints.cs:20-99`) buscan la regla **solo por Id**, sin validar el estudio. Es un IDOR cross-tenant preexistente: con el ID de una regla de otro estudio, un `StudioOwner` puede editarla o borrarla.

Una feature de "copiar/pegar reglas" que recibe `ruleIds[]` amplifica esto directamente. **Hay que cerrarlo en el mismo epic**, con `HasQueryFilter` sobre `AccountingRule` (ancla: `StudioTenantId` cuando `CompanyId` es null; join a `Company` cuando no) o, más simple y consistente con P-2, desnormalizando `StudioTenantId` en `AccountingRule` para todas las reglas y anclando ahí.

### 5.2 Diseño

```
POST /api/companies/{targetCompanyId}/rules/copy
Body: { sourceCompanyId: guid, ruleIds: [guid], onConflict: "skip" | "overwrite" }
Response: { copied: N, skipped: [{ keyword, reason }] }
```

- Validar que **origen y destino pertenecen al mismo `StudioTenantId`**.
- Conflictos: la lógica ya existe client-side en `rules-page.ts:304-316` (`keywordsOverlap` + `directionsCompatible`). Reusar ese criterio server-side.
- **Cuota**: `CreateCompanyRuleCommand` valida cuota del plan (`CompanyEndpoints.cs:124`). La copia masiva tiene que validarla *antes* del lote, no regla por regla, para no dejar copias a medias.
- `AccountingRule.Keyword`/`TargetAccount`/`Priority`/`CompanyId` son `init` → la copia es siempre una entidad nueva. Sin problema.
- **`TargetAccount` no necesita traducción**: `ChartOfAccount` es por *estudio* (`ChartOfAccount.cs:19`), no por empresa. Dentro del mismo estudio el nombre de cuenta siempre resuelve. Si algún día se copia entre estudios, esto se rompe.

### 5.3 Frontend

- `rules-table`: checkbox de selección múltiple + acción "Copiar a otra empresa…".
- Modal: selector de empresa destino (`companyService.companies()`, ya disponible) + preview de conflictos.
- `rule.service.ts`: `copyRules(targetCompanyId, sourceCompanyId, ruleIds)`.

### 5.4 Pregunta de diseño

**Ya existen reglas a nivel estudio** (`AccountingRule.StudioTenantId`, `GET/POST /api/studio/rules`, `studio-rules-page`) que aplican automáticamente a *todas* las empresas del estudio, con precedencia Empresa > Estudio > Sistema (`HardRuleStrategy.cs:37-57`).

Para el caso "esta regla sirve para todos mis clientes", **promover a regla de estudio es estrictamente mejor que copiarla N veces**: una sola fila, se edita en un lugar, no hay drift. Copiar tiene sentido para "sirve para estas 3 de 40 empresas".

Recomiendo implementar **ambas**: "Copiar a empresa…" y "Promover a regla de estudio". El segundo es más barato que el primero y probablemente resuelva el 70% de los casos reales. ¿Lo confirmás con el usuario final antes de que construyamos el copy/paste completo?

---

## 6. Edge cases

### 6.1 Los que preguntaste

**Transacciones huérfanas viejas sin cuenta bancaria.**
`BankAccountId` nullable + backfill por (empresa, moneda) resuelve la mayoría. Quedan sin cuenta: (a) movimientos con `CompanyId = null` (bucket legacy `ESTUDIO_DEFAULT`, ya invisibles por el query filter de `ContableAIDbContext.cs:323-324`), y (b) empresas que nunca configuraron `BankAccountName`. Tratamiento: bucket **"Sin cuenta asignada"** en el filtro de la grilla, con acción bulk "Asignar a cuenta…". No se pueden asentar — que es exactamente lo que ya pasa hoy.

**El OCR no lee el número por mala calidad del PDF.**
Cascada de §1.4: cuenta explícita del usuario → detección → error por archivo. El canal `parseErrors` (`UploadBankStatementHandler.cs:206`, `:465`) ya existe y la UI ya lo muestra, así que el mensaje "No se pudo detectar la cuenta en 'extracto-marzo.pdf'. Seleccioná la cuenta manualmente." no requiere infra nueva. **Nunca adivinar por el número de cuentas de la empresa** aunque tenga una sola: si mañana agrega una segunda, los movimientos quedaron mal ruteados retroactivamente y nadie se entera.

### 6.2 Los que no preguntaste y me preocupan más

**Transferencias entre cuentas propias — doble contabilización.**
`GlobalRules.cs:69` ya tiene reglas para "transferencias entre cuentas propias". Hoy, con una cuenta por empresa, solo se importa un lado. Con multi-cuenta, si el usuario sube los dos extractos, el sistema importa **ambas patas** de la transferencia y genera **dos asientos** — inflando el movimiento. Necesitamos detección de contrapartida interna (mismo importe, ±1 día, dos cuentas de la misma empresa, signos opuestos) y una marca `IsInternalTransfer` que evite el doble asiento. **Esto solo lo puede validar un contador. Es la pregunta más importante de todo el paquete.**

**Firma de deduplicación sin cuenta bancaria.**
Ya cubierto en §0.4 y §1.4. Repito porque es pérdida silenciosa de datos.

**El PDF con dos cuentas adentro.**
`PdfBankParser` ya rechaza extractos con dos monedas (`MixedCurrencyError`, `:160-162`). Un PDF con dos cuentas de la *misma* moneda (común en resúmenes consolidados de Galicia/Credicoop) hoy pasa sin problema y, con enrutamiento a nivel documento, quedaría todo colgando de la primera cuenta detectada. Mínimo: detectar N números de cuenta distintos y rechazar con mensaje claro, igual que con las monedas.

**Un mismo CBU en dos empresas del mismo estudio.**
Pasa en grupos económicos. El índice único es por `(CompanyId, NormalizedNumber)`, no global — correcto. Pero si la Dropzone no tiene empresa seleccionada, el enrutamiento es ambiguo. **Decisión: el enrutamiento automático requiere empresa seleccionada.** Cruzar empresas automáticamente en un mismo lote es demasiado riesgo para el beneficio.

**OCR y confusión de dígitos (0/O, 1/l, 5/S, 8/B).**
Normalizar a solo dígitos y matchear por **los últimos 6-8 dígitos**, verificando que resuelva a una sola cuenta. Si matchea 2+, tratarlo como "no detectado" (cae al error por archivo). Nunca elegir la "más parecida".

**Cambiar la contrapartida de una cuenta con asientos ya generados.**
Los `JournalEntryLine` ya emitidos siguen con el nombre viejo. Igual que hoy con `Company.BankAccountName`. Hay que decidir si se advierte ("hay 340 asientos con la cuenta anterior") o se ofrece regenerar. Mínimo: advertir.

**Desactivar una cuenta bancaria.**
Soft-delete obligatorio (`IsActive`), nunca borrado: hay movimientos y asientos colgando. Y `StudioTenantPurger` / `DeleteStudioTenantHandler` tienen que incluir `BankAccounts` en el purgado, si no la baja de estudio deja filas huérfanas.

---

## 7. Plan de ejecución

**Principio de ordenamiento:** F1 toca *todo* (modelo, parsers, upload, generador, 4 endpoints de export, 2 grillas). Cualquier feature que se meta después de arrancar F1 va a chocar. Por eso F1 va **al final**, y todo lo barato se cierra antes.

### Sprint 1 — Quick wins (3-4 días)

**Epic A — Export UX (F2)** · ½ día
1. `WithExposedHeaders("Content-Disposition")` en CORS.
2. Leer el header en `journal-entry.service.ts` (4 métodos de descarga) y en `transaction.ts:225`.
3. Test e2e de que el nombre descargado trae la empresa.

**Epic B — Split Debe/Haber (F3)** · 1-2 días
1. Generalizar `groupKeyFor`/`groupLabelFor` en `journal-page.ts`.
2. Espejar en `ExcelExportService.BuildFormularioSheet`.
3. Borrar `balanceAccounts` de la interfaz y de los 2 endpoints.
4. Test de paridad UI ↔ Excel sobre fixture con cuenta en ambos lados.

> A y B son independientes: pueden ir en paralelo con dos personas.

### Sprint 2 — Reglas (5-6 días)

**Epic C — Hardening de reglas** · 1 día · **prerequisito de D y E**
- Global Query Filter sobre `AccountingRule` (cierra el IDOR de §5.1).
- Unificar el matching de keyword con `HardRuleStrategy.DescriptionMatchesKeyword`.

**Epic D — Override retroactivo (F4)** · 2 días
- Nuevo filtro de candidatos, exclusiones (`AfipComboMatch`, períodos cerrados).
- `dryRun` + desglose por origen.
- Confirmación destructiva en la UI + `AuditLog`.
- Batching / Hangfire si el volumen lo justifica.

**Epic E — Portabilidad (F5)** · 2 días
- `POST /rules/copy` con validación de estudio y cuota.
- "Promover a regla de estudio" (barato, alto valor).
- Selección múltiple + modal en `rules-table`.

### Sprints 3-5 — Multi-cuenta (F1) · 2-3 semanas

Cinco fases, cada una desplegable por separado:

**F1.a — Modelo y migración** (3 días)
Entidad, `DbContext`, índices, migración con backfill. **Deploy sin cambios de comportamiento**: nada lee `BankAccountId` todavía. Permite validar el backfill en producción con riesgo cero.

**F1.b — CRUD y ficha de empresa** (3 días)
Endpoints + pestaña "Cuentas bancarias" en `company-modal`. Los strings viejos siguen siendo la fuente de verdad. El usuario ya puede dar de alta sus cuentas.

**F1.c — Generación de asientos por cuenta** (3 días)
`TryResolveBankAccount` pasa de resolver-por-moneda a resolver-por-cuenta. Validación 422 por cuenta. **Punto de no retorno**: acá `Company.BankAccountName` deja de usarse.

**F1.d — Parsers, OCR y enrutamiento** (5 días) — *la parte más riesgosa*
`ParsedStatement`, detección de número de cuenta, `BankAccountId` en la firma de dedup, enrutamiento en el handler, cuentas provisionales. Validar contra los fixtures reales de `tests/extractos/` (BBVA, Galicia, Credicoop, Ciudad, MercadoPago) — **hay que medir la tasa de detección por banco antes de mergear**; si algún banco queda por debajo de ~90%, ese banco arranca en modo manual.

**F1.e — Grillas, filtros y columna dinámica** (4 días)
Query params, `AvailableBankAccounts`, filtros y columna condicional en movimientos y asientos, export por cuenta.

---

## 8. Decisiones que necesito de vos antes de arrancar

1. **Transferencias entre cuentas propias** (§6.2): ¿cómo debe comportarse contablemente? Bloqueante para F1.d.
2. **Cuenta provisional vs. modal previo** (§1.4): recomiendo provisional. ¿Confirmás?
3. **`AfipComboMatch` excluido del override retroactivo** (§4.3b): recomiendo excluirlo. ¿Confirmás?
4. **¿Reapply para reglas de estudio/globales** (§4.6) entra en v1.1?
5. **Copiar reglas vs. promover a regla de estudio** (§5.4): ¿validaste con el usuario final cuál necesita realmente?
6. **Modal de nombre de archivo** (§2): recomiendo no hacerlo. ¿De acuerdo?
