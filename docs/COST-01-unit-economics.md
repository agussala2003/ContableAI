# COST-01 · Modelo de costo unitario por cliente

> **Objetivo:** saber cuánto cuesta servir a UN cliente por mes, para fijar el piso de precio
> (ningún plan debe venderse por debajo del costo) y validar con números la estrategia de volumen.
> **Última actualización:** 2026-06-08 · **Estado:** vivo (actualizar cuando cambien planes/precios).

---

## 0. Decisión comercial adoptada (2026-06-08)

- **Infra de arranque:** **Render Starter ($7, always-on)** + Neon/Vercel/Resend en **Free** → **fijo ~$8/mes**.
  El Starter evita los cold starts y arregla Hangfire (cruce AFIP) por $7.
- **Plan de entrada: "Pro" a US$20/mes** (= límites del enum `StudioPlan.Pro`: 15 empresas / 250 reglas / tx ilimitadas).
- **Enterprise: a medida** ("Hablá con nosotros").
- **Break-even: ~1 cliente** (fijo $8 ÷ $20). Cada Pro adicional suma ~$20 de margen (marginal ~$0).

| Clientes Pro | Ingreso | Costo fijo | Margen |
|---|---|---|---|
| 1 | $20 | $8 | $12 (60%) |
| 5 | $100 | $8 | $92 (92%) |
| 10 | $200 | $8 | $192 (96%) |

**Disparadores de upgrade:** Render Starter 512 MB → Standard $25 si el OCR se queda sin RAM; Neon Free 0.5 GB → Launch $19 cuando se llene el storage.

---

## 1. Estructura de costos (anclada en el stack real)

Verificado en el código, no son supuestos:

### Costo MARGINAL por cliente ≈ $0
| Componente | Por qué es ~$0 |
|---|---|
| **OCR** | Tesseract **local** (`PdfBankParser.cs`, `EngineMode.LstmOnly`, "spa+eng"). Corre en CPU propia, sin costo por uso. Solo fallback en PDFs escaneados. |
| **Clasificación** | Motor de **reglas** (245+). No hay IA paga; el `ClassificationSources.Ai` está marcado `[Obsolete("Not used in MVP")]`. **$0 por extracto.** |
| **Email** | **Resend** (`smtp.resend.com`), free tier 3.000/mes. Solo mails transaccionales (reset de pass). Entra gratis. |
| **Storage por cliente** | Postgres en Neon: centavos por GB. Extractos + movimientos + asientos crecen lento. |

→ **Marginal real ≈ $0.** El único costo que crece con el uso son *escalones de infraestructura* (CPU de OCR, storage), no un cargo por transacción.

### Costo FIJO mensual (se reparte entre TODOS los clientes)
- **Render** — API + worker Hangfire + CPU de OCR.
- **Neon** — Postgres (storage + compute).
- **Vercel** — frontend.
- **Dominio** — ~$1/mes prorrateado.

→ **Economía de costo fijo alto / marginal casi cero.** Es la economía SaaS que **premia el volumen**:
el costo por cliente = `Fijo total ÷ Nº de clientes`, y **se desploma** al sumar clientes.

---

## 2. Estado actual (jun 2026): tiers GRATUITOS

Hoy Render y Neon están en plan **Free** → **costo fijo actual = $0/mes**.

⚠️ **El "precipicio" del tier gratuito** (lo va a tocar el primer cliente real):
- **Render Free:** 512 MB RAM, 0.1 CPU, **se apaga tras 15 min de inactividad** (cold start ~30 s). Problemas reales:
  - El **OCR (Tesseract) es RAM-hungry** → 512 MB puede quedar corto en PDFs escaneados.
  - **Hangfire** (cruce AFIP automático) **no corre con el servicio apagado** → el cruce se demora hasta que una request lo despierta.
- **Neon Free:** 0.5 GB storage, autosuspend a los 5 min (primera query lenta). El storage se llena con pocos clientes activos.

→ **Conclusión:** hoy el costo/cliente es literalmente **$0**, pero el Free tier aguanta solo **demo / 1-2 clientes livianos**. El primer cliente pago obliga a saltar a tiers pagos. **El número de planeación real es el escenario pago de abajo.**

---

## 3. Escenario PAGO (cuando lleguen clientes)

Precios de lista aproximados (verificar al contratar):

| Componente | Plan | Costo/mes |
|---|---|---|
| Render | Standard (2 GB / 1 CPU — headroom para OCR) | ~$25 |
| Neon | Launch (10 GB + autoscaling) | ~$19 |
| Vercel | Pro (requerido para uso comercial) | ~$20 |
| Resend | Free → Pro si supera 3k/mes | $0–20 |
| Dominio | prorrateado | ~$1 |
| **FIJO TOTAL** | | **~$65–85/mes** |

---

## 4. Costo por cliente y break-even

**Costo por cliente = Fijo ÷ N**

| N clientes | Free hoy ($0) | Pago (~$80) |
|---|---|---|
| 1–2 | $0 | (forzás el salto a pago) |
| 5 | — | $16.0 |
| 10 | — | $8.0 |
| 30 | — | $2.7 |
| 50 | — | $1.6 |

**Break-even** (básico a precio P): cubrís el fijo cuando **`N × P ≥ Fijo`**.

| Básico (P) | Fijo $80 → break-even |
|---|---|
| $10 | 8 clientes |
| $15 | ~6 clientes |
| $20 | 4 clientes |

→ A partir del break-even, como el marginal es ~$0, **casi todo ingreso adicional es margen.** Esto valida
la estrategia de Seba: precio de entrada bajo + volumen = muy rentable una vez pasado el break-even.

---

## 5. Hallazgos accionables (salieron de revisar el código)

1. **EPPlus en `LicenseContext.NonCommercial`** pero ContableAI es producto **comercial** → incumplimiento
   de licencia + costo oculto (~US$599/año si se regulariza).
   **→ Acción recomendada: migrar a ClosedXML (MIT, gratis).** Elimina el costo y el riesgo legal.
2. **OCR (Tesseract) define el tier de Render.** No cuesta por uso, pero es pesado en RAM/CPU: el "costo
   variable" real son escalones de infraestructura. Si muchos clientes suben escaneados a la vez → Standard+.

---

## 6. Disparadores de re-evaluación de tier (escalones)

| Trigger | Acción |
|---|---|
| **Primer cliente pago** | Salir de Render Free (cold start + Hangfire) → Starter/Standard. |
| OCR falla por RAM | Render Standard (2 GB). |
| Storage Neon > ~0.4 GB | Neon Launch. |
| Uso comercial real | Vercel Pro (ToS) + regularizar Excel (ClosedXML). |
| Email > 3.000/mes | Resend Pro. |

---

## 7. Supuestos a mantener actualizados
- Planes reales contratados (hoy: **Render Free + Neon Free = $0**).
- Precio del plan básico (a definir en ENTRY-01, depende de este piso).
- Nº de clientes activos (recalcula costo/cliente y break-even).

> **Próximo paso ligado:** con este piso, definir **ENTRY-01** (modelo de entrada sin plan Free) y el
> precio del básico, que alimentan el CTA de la **landing (P1-3)**.
