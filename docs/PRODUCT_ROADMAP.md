# ContableAI — Roadmap Estratégico de Producto

> **Visión:** Convertir ContableAI en la **Súper App** definitiva para estudios contables argentinos: la herramienta que reemplaza el Excel, elimina la burocracia manual y le devuelve tiempo al contador.

---

## Estado Actual del Producto (Baseline — Q1 2026)

ContableAI ya es un sistema funcional en producción con una arquitectura Clean Architecture sólida (`.NET 10` + `Angular 21` + `PostgreSQL`) que incluye:

- **Parsers multi-banco** (BBVA, CSV/XLSX genérico, PDF bancario)
- **Motor de reglas keyword-based** con 245+ reglas globales predefinidas
- **Cruce AFIP / VEP** contra extractos bancarios (tolerancia ±2 días)
- **Generación de asientos de partida doble** con validación de períodos cerrados
- **Exportación multi-formato** (Excel, Holistor, Bejerman)
- **Multi-tenancy** (estudio → empresa → usuario) con planes Free / Pro / Enterprise
- **Dashboard de KPIs** con filtros por mes y año
- **Auditoría completa** de todas las operaciones

---

## Principios del Roadmap

1. **Valor para el Estudio, primero.** El contador es el usuario pagador. Cada feature debe reducir horas de trabajo o prevenir errores costosos.
2. **Diferenciación local.** El mercado argentino tiene AFIP, ARCA, IIBB, Holistor, Bejerman — conocerlos en profundidad es nuestra ventaja competitiva irreplicable.
3. **Simplicidad antes que poder.** Features potentes con UX de producto de consumo masivo.
4. **Datos compuestos.** Cada feature genera datos que alimentan los siguientes.

---

## Mapa de Iniciativas (9 en total)

| # | Iniciativa | Impacto | Esfuerzo | Prioridad |
|---|-----------|---------|----------|-----------|
| 1 | Conciliación Mágica (AFIP ↔ Banco 100%) | Muy Alto | Alto | P0 |
| 2 | Aprendizaje Proactivo (sugerencia de reglas) | Alto | Medio | P0 |
| 3 | Experiencia "Modo Excel" en el grid | Alto | Medio | P0 |
| 4 | Reglas Globales de Estudio (cascada multi-empresa) | Alto | Bajo | P1 |
| 5 | Onboarding Interactivo (FTUE) | Alto | Medio | P1 |
| 6 | Landing Page Comercial | Medio | Bajo | P1 |
| 7 | *(NUEVO)* Portal del Cliente | Muy Alto | Alto | P2 |
| 8 | *(NUEVO)* Cash Flow Predictivo y Alertas de Anomalías | Muy Alto | Alto | P2 |
| 9 | *(NUEVO)* Integración API Pública + Webhooks ERP | Alto | Medio | P2 |

---

## P0 — Fundaciones del Core (Corto Plazo)

### 1. Conciliación Mágica

**Dolor que resuelve:** El cruce manual entre lo que AFIP/ARCA muestra como retenciones, percepciones e impuestos y los débitos reales en el extracto bancario consume horas por empresa. El sistema actual ya soporta cruce por VEP, pero no es exhaustivo ni automático al 100%.

**Descripción:**
- Cruce automático, en batch, entre **todos** los movimientos del extracto y los comprobantes disponibles en AFIP (VEPs pagados, retenciones sufridas, percepciones).
- Matching por importe exacto **y** tolerancia de ±2 días de fecha.
- Panel dedicado "Conciliación" que muestra el estado visual: ✅ Cruzado / ⚠️ Discrepancia / ❌ Sin cruzar.
- Detección de **duplicados de pago** (mismo impuesto, mismo importe, dos débitos distintos).
- Generación automática de asiento por cada VEP cruzado.

**Impacto esperado:** Reducción del tiempo de cierre mensual de ~6 horas a ~30 minutos por empresa con alta carga tributaria.

**Componentes técnicos:**
- Nuevo endpoint `GET /api/reconciliation/summary` que devuelve items cruzados/pendientes/discrepantes.
- Ampliar `PdfAfipParserService` para soportar el formato de constancia de retenciones IIBB.
- Nuevo componente Angular `reconciliation-summary-panel` en la feature `reconciliation`.

---

### 2. Aprendizaje Proactivo (Sugerencia Automática de Reglas)

**Dolor que resuelve:** El contador clasifica manualmente la misma descripción repetida 3 meses seguidos porque nunca se detiene a crear la regla. El sistema tiene toda la información para hacerlo por él.

**Descripción:**
- Background job (diario/semanal) que analiza transacciones clasificadas manualmente (`ClassificationSource = "Manual"`).
- Si detecta que la misma descripción fue asignada a la misma cuenta **≥3 veces** en los últimos 90 días → genera una sugerencia de regla.
- Notificación in-app: _"Detectamos que 'BBVA COMISIONES' siempre se asigna a 'Gastos Bancarios'. ¿Crear regla automática?"_ con botón de un click para confirmar.
- Panel de "Sugerencias Pendientes" en la página de Reglas.
- Posibilidad de rechazar una sugerencia (no volver a mostrar para esa keyword).

**Impacto esperado:** Reducción del 40–60% en clasificaciones manuales repetitivas tras 2 meses de uso.

**Componentes técnicos:**
- Nuevo `Command`: `SuggestRulesFromPatternsCommand` (MediatR, ejecutable vía endpoint o scheduler).
- Nueva entidad `RuleSuggestion` (keyword, suggestedAccount, frequency, status: pending/accepted/rejected).
- Nuevo endpoint `GET /api/rule-suggestions` y `POST /api/rule-suggestions/{id}/accept`.
- Badge de notificación en el nav-item "Reglas".

---

### 3. Experiencia "Modo Excel" en el Grid de Transacciones

**Dolor que resuelve:** Los contadores viven en Excel. El flujo actual requiere múltiples clicks para clasificar una transacción. Con 200+ transacciones por mes, eso es cientos de clicks innecesarios.

**Descripción:**
- **Navegación por teclado:** `↑/↓` para moverse entre filas, `Enter` para editar la cuenta, `Escape` para cancelar, `Tab` para avanzar al siguiente campo.
- **Selección múltiple:** `Shift+Click` para seleccionar rango, `Ctrl+Click` para selección individual. Barra de acción flotante aparece al seleccionar múltiples filas.
- **Edición ultra-rápida:** Al presionar `Enter` en la celda de cuenta, se abre un autocomplete inline (no modal) que filtra el plan de cuentas mientras se tipea.
- **Bulk assign desde selección:** Seleccioná 15 filas de "MERCADOPAGO" → un click → asignar cuenta a todas.
- **Undo/Redo:** `Ctrl+Z` para deshacer la última asignación de cuenta.

**Impacto esperado:** Reducción del tiempo de clasificación manual de ~45 min a ~10 min por mes para estudios con >100 tx.

**Componentes técnicos:**
- Refactor del componente `transaction-grid` para manejar estado de selección con `Set<string>` de IDs.
- Directiva Angular `KeyboardNavigable` para manejar eventos de teclado.
- Componente `InlineAccountSelect` con Virtual Scroll para performance con 500+ cuentas.
- Estado de historial de cambios (`UndoStack`) como Signal.

---

## P1 — Crecimiento y Retención (Mediano Plazo)

### 4. Reglas Globales de Estudio (Cascada Multi-Empresa)

**Dolor que resuelve:** Un estudio con 15 empresas del mismo rubro (ej: gastronómico) repite la misma configuración de reglas 15 veces. Cambiar una alícuota de IIBB implica editar 15 empresas por separado.

**Descripción:**
- Nuevo nivel en la jerarquía de reglas: **Reglas de Estudio** (por encima de las de empresa, por debajo de las globales del sistema).
- Las reglas de estudio se crean desde el panel Admin y se aplican automáticamente a todas las empresas del tenant.
- Override por empresa: Una empresa puede "pisar" una regla de estudio con su propia regla de mayor prioridad.
- Visualización clara en la UI: Las reglas muestran su origen (Sistema | Estudio | Empresa) con color diferenciado.
- Endpoint `POST /api/studio/rules` para CRUD de reglas de estudio.

**Impacto esperado:** Reducción del tiempo de setup de nuevas empresas de ~30 min a ~5 min para estudios con segmentos de clientes homogéneos.

**Componentes técnicos:**
- Modificar `AccountingRule` para agregar campo `Scope: Global | Studio | Company`.
- Actualizar `HardRuleStrategy` para incorporar el nivel "Studio" en la jerarquía de precedencia.
- Sección "Reglas del Estudio" en la página Admin.

---

### 5. Onboarding Interactivo (First-Time User Experience)

**Dolor que resuelve:** El mayor problema de activación en SaaS B2B: el usuario se registra, ve una pantalla vacía y no sabe por dónde empezar. El contador necesita ver valor en los primeros 10 minutos.

**Descripción:**
- Al crear la primera cuenta, se dispara una guía visual paso a paso (no intrusiva, con overlay).
- **Paso 1:** Crear la primera empresa (con template pre-cargado del plan de cuentas más común).
- **Paso 2:** Subir el primer extracto bancario (con archivo de ejemplo descargable si no tiene uno a mano).
- **Paso 3:** Ver las transacciones clasificadas automáticamente y ajustar 2-3 manualmente.
- **Paso 4:** Generar el primer asiento contable.
- **Paso 5:** Exportar a Excel — "¡Tu primer cierre listo en menos de 5 minutos!".
- Progress indicator persistente (puede pausar y retomar).
- Botón "Saltar tour" siempre visible.

**Impacto esperado:** Incremento del 30–50% en la tasa de activación (usuarios que completan el primer ciclo completo en los primeros 7 días).

**Componentes técnicos:**
- Servicio `OnboardingService` que persiste el estado del tour en `localStorage` + backend (para no repetirlo tras logout).
- Componente `OnboardingOverlay` standalone con portal rendering.
- Flag `HasCompletedOnboarding` en entidad `User`.

---

### 6. Landing Page Comercial

**Dolor que resuelve:** La adquisición orgánica es imposible sin presencia web pública. Actualmente no existe forma de que un contador encuentre ContableAI sin una referencia directa.

**Descripción:**
- Sitio público (subdominio `www` o raíz del dominio) con:
  - **Hero:** Propuesta de valor clara ("Cierra el mes en 30 minutos, no en 3 días").
  - **Screenshots interactivos** del dashboard, grid de transacciones y exportación.
  - **Sección de beneficios** diferenciados (integración AFIP, reglas inteligentes, exporta a Holistor/Bejerman).
  - **Planes y precios** claros (Free / Pro / Enterprise) con comparativa de features.
  - **Social proof:** Testimonios de contadores early adopters.
  - **CTA:** Registro gratuito sin tarjeta de crédito.
- SEO-optimizado: meta tags, schema.org, sitemap.xml.
- Blog/recursos: artículos sobre cierre contable, AFIP, herramientas para estudios.

**Impacto esperado:** Canal de adquisición orgánica SEO con objetivo de 200 visitas/mes al tercer mes de lanzamiento.

**Componentes técnicos:**
- Proyecto Angular separado (o Next.js para mejor SSR/SEO) en `/landing`.
- Integración con formulario de registro que llama al endpoint `POST /api/auth/register` existente.

---

## P2 — Diferenciación Estratégica (Largo Plazo)

### 7. *(NUEVO)* Portal del Cliente: Colaboración Estudio-Empresa en Tiempo Real

**Dolor que resuelve:** El mayor cuello de botella operativo de un estudio contable hoy es la **comunicación con el cliente**: el contador pide los extractos por WhatsApp, el cliente los manda en PDF por mail, el contador los descarga, los sube al sistema. Este ciclo se repite cada mes por cada empresa. Para un estudio con 20 clientes, son 20 conversaciones de WhatsApp por mes. Además, el cliente nunca sabe el estado de su contabilidad.

**Descripción:**
- Portal web dedicado para los **clientes del estudio** (no contadores, sino los dueños de empresa).
- Acceso simplificado (link por email o QR, sin crear cuenta compleja).
- **Lo que puede hacer el cliente:**
  - Subir sus propios extractos bancarios directamente (drag & drop con instrucciones visuales).
  - Ver el estado de procesamiento en tiempo real ("Estamos clasificando tus movimientos ✅").
  - Aprobar o rechazar asientos pendientes antes de que el contador los finalice.
  - Descargar sus propios reportes (Excel del mes, resumen de gastos por categoría).
  - Ver un resumen ejecutivo de su situación financiera (totales de ingresos, egresos, impuestos del mes).
- **Lo que gana el estudio:**
  - Notificación automática cuando el cliente sube archivos nuevos.
  - Menos mensajes de WhatsApp con "¿cómo está la contabilidad?".
  - Diferencial comercial frente a estudios que trabajan 100% por email.

**Impacto esperado:** Reducción del 70% en la coordinación manual estudio-cliente. Incremento del Net Promoter Score porque el cliente ve valor directo.

**Componentes técnicos:**
- Nueva entidad `ClientPortalToken` (token de acceso por empresa, con expiración configurable).
- Nuevo `UserRole: ClientViewer` (permisos de solo lectura + upload propio).
- Feature Angular `/portal/:token` (ruta pública con auth por token).
- Nuevo endpoint `POST /api/companies/{id}/portal-invitations` que genera y envía el link por email.
- Notificaciones por email (SMTP ya configurado en el sistema) cuando el cliente sube archivos.

---

### 8. *(NUEVO)* Cash Flow Predictivo y Detector de Anomalías Tributarias

**Dolor que resuelve:** Hoy ContableAI registra el pasado. El contador cierra el mes, pero **no puede prevenir** el futuro. Los clientes llegan al estudio con sorpresas: "¿Cómo que no me alcanza para pagar los impuestos este mes?" o "¿Por qué me retuvieron más IIBB que el mes pasado?". El 80% de los problemas financieros de las PyMEs son predecibles con los datos correctos.

**Descripción:**

**Módulo A — Cash Flow Predictivo:**
- Basado en el historial de transacciones clasificadas (ya almacenado en el sistema), genera automáticamente una **proyección de flujo de caja para los próximos 30/60/90 días**.
- Detecta **patrones estacionales**: "Esta empresa siempre tiene un pico de ingresos en diciembre y una caída en enero".
- Alerta proactiva: "⚠️ Según el historial, en los próximos 15 días tiene pagos estimados por $X. Su saldo promedio en esta fecha suele ser $Y. Riesgo de descubierto."
- Gráfico de área: línea de ingresos proyectados vs egresos proyectados vs saldo estimado.

**Módulo B — Detector de Anomalías Tributarias:**
- El sistema conoce las cuentas contables asignadas a cada transacción. Puede detectar:
  - **Percepciones/retenciones que no corresponden** al importe declarado (ej: te retienen IIBB al 3% pero la alícuota correcta para tu CUIT es 1.5%).
  - **Gastos atípicos:** Un gasto en categoría X que es >150% del promedio histórico de los últimos 6 meses → alerta al contador.
  - **Dobles pagos de impuesto:** Dos débitos de AFIP en el mismo mes con el mismo tipo de impuesto.
  - **Ingreso no declarado:** Crédito bancario significativo sin transacción de facturación correspondiente.
- Panel "Alertas del Mes" visible en el Dashboard con severidad (Alta / Media / Baja).

**Impacto esperado:** Este módulo convierte a ContableAI de una herramienta de registro a un **asesor financiero proactivo**. Es el mayor diferencial de producto posible: ningún competidor en el mercado argentino PyME ofrece predicción de cash flow integrada con la contabilidad real.

**Componentes técnicos:**
- Servicio `CashFlowProjectionService` (algoritmo de media móvil por categoría de cuenta + detección de outliers estadísticos).
- Nuevo endpoint `GET /api/companies/{id}/cashflow-projection?months=3`.
- Nueva entidad `AnomalyAlert` (type, severity, description, transactionId, resolvedAt).
- Nuevo endpoint `GET /api/companies/{id}/anomalies` y `POST /api/anomalies/{id}/dismiss`.
- Componente Angular `cashflow-chart` (chart de área con proyección futura).
- Widget de alertas en el Dashboard existente.

---

### 9. *(NUEVO)* API Pública + Webhooks para Integración con Ecosistemas ERP

**Dolor que resuelve:** Hoy la exportación es 100% manual: el contador descarga un Excel o TXT y lo importa manualmente a Holistor, Bejerman o a cualquier sistema que use el cliente. Con 20 empresas × 12 meses = 240 exportaciones manuales anuales. Además, algunos estudios grandes ya tienen herramientas internas (BI, dashboards propios) y necesitan los datos en tiempo real.

**Descripción:**

**API Pública:**
- Documentación pública en `docs.contableai.app` (Swagger/Scalar existente, expuesto con autenticación por API Key).
- **Endpoints habilitados para terceros:**
  - `GET /api/v1/transactions` — datos de transacciones clasificadas.
  - `GET /api/v1/journal-entries` — asientos generados.
  - `POST /api/v1/transactions/upload` — subida de extractos desde sistemas externos.
- Autenticación por **API Key** (diferente al JWT de usuario, con scopes: `read:transactions`, `write:transactions`, `read:journal`).
- Rate limiting por plan (Free: 100 req/día, Pro: 5k req/día, Enterprise: ilimitado).

**Webhooks:**
- El estudio configura una URL de destino para recibir eventos en tiempo real:
  - `journal_entry.generated` — payload con el asiento completo en JSON.
  - `transaction.classified` — cuando una transacción cambia su cuenta asignada.
  - `period.closed` — cuando se cierra un período contable.
- **Caso de uso estrella:** Integración directa con Holistor/Bejerman sin exportación manual. El webhook dispara un script del cliente que importa el asiento automáticamente.
- Panel de configuración de webhooks con log de deliveries y retry automático (3 intentos con backoff exponencial).

**Impacto esperado:** Desbloquea un mercado de estudios contables grandes con infraestructura propia que hoy no pueden usar la herramienta por la fricción de la exportación manual. Crea un ecosistema de integradores. Potencial para ingresos adicionales por volumen de API.

**Componentes técnicos:**
- Nueva entidad `ApiKey` (tenantId, keyHash, name, scopes[], lastUsedAt, expiresAt).
- Nueva entidad `WebhookSubscription` (tenantId, url, events[], secret, active).
- Nueva entidad `WebhookDelivery` (log de intentos, status, responseCode, nextRetryAt).
- Servicio `WebhookDispatchService` (IHostedService que consume cola de eventos con retry).
- Nuevos endpoints bajo `/api/v1/` con autenticación por API Key.
- Sección "Integraciones" en el panel Admin.

---

## Timeline Propuesto

```
2026
├── Q2 (Abr–Jun)
│   ├── [P0] Experiencia "Modo Excel" en grid         ← Quick win de retención
│   ├── [P0] Aprendizaje Proactivo                    ← Quick win de valor percibido
│   └── [P1] Reglas Globales de Estudio               ← Quick win para estudios
│
├── Q3 (Jul–Sep)
│   ├── [P0] Conciliación Mágica completa             ← Feature flagship
│   ├── [P1] Onboarding Interactivo (FTUE)            ← Activación
│   └── [P1] Landing Page Comercial                   ← Adquisición
│
└── Q4 (Oct–Dic)
    ├── [P2] Portal del Cliente                        ← Diferenciación #1
    ├── [P2] Cash Flow Predictivo + Anomalías          ← Diferenciación #2
    └── [P2] API Pública + Webhooks                   ← Diferenciación #3

2027
└── Q1+
    └── IA Contable (clasificación LLM sin reglas)    ← Next frontier
```

---

## Métricas de Éxito por Iniciativa

| Iniciativa | Métrica Principal | Target |
|-----------|------------------|--------|
| Conciliación Mágica | % VEPs cruzados automáticamente | ≥ 95% |
| Aprendizaje Proactivo | Reducción de clasificaciones manuales repetidas | -40% en 60 días |
| Modo Excel | Tiempo promedio de clasificación de 100 tx | < 8 min |
| Reglas Globales | Tiempo de setup de empresa nueva | < 5 min |
| Onboarding | Tasa de activación (completa ciclo en 7 días) | ≥ 60% |
| Landing Page | Registros orgánicos mensuales | ≥ 50/mes en M3 |
| Portal del Cliente | NPS de clientes del estudio | ≥ 8/10 |
| Cash Flow / Anomalías | Alertas accionables por empresa/mes | ≥ 3 (recall ≥ 80%) |
| API / Webhooks | Estudios con integración activa | ≥ 10 en Q4 2026 |

---

## Apéndice: Deuda Técnica Prioritaria

Estos no son features sino inversiones de plataforma que desbloquean el roadmap:

1. **Sistema de notificaciones in-app** — necesario para Aprendizaje Proactivo, Alertas de Anomalías y Portal del Cliente.
2. **Background jobs con persistencia** (Hangfire o similar) — necesario para análisis de patrones, webhooks con retry y proyección de cash flow.
3. **Websockets / Server-Sent Events** — necesario para el estado en tiempo real del Portal del Cliente.
4. **Versionado de API (`/api/v1/`)** — necesario antes de exponer la API pública.
5. **Cobertura de tests de integración >70%** — requerido antes de abrir la API a terceros.

---

*Documento creado: 2026-03-31 | Autor: ContableAI Tech/Product Team*
