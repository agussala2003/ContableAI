# ENTRY-01 · Modelo de entrada del cliente (sin plan Free)

> **Objetivo:** definir cómo un prospecto pasa de la landing a cliente pago, dado que **NO hay plan Free**.
> Alimenta el CTA de la landing (P1-3) y depende del piso de precio de [COST-01](COST-01-unit-economics.md).
> **Decisión (2026-06-08):** estrategia escalonada **A ahora → B pronto → C después**.
> **Estado:** Fase A definida (cero código); Fase B especificada (pendiente de build).

---

## 1. Estado actual del flujo (hallazgos del código)

Hay **dos caminos de registro** y un gap:

| Camino | Estado inicial | Resultado |
|---|---|---|
| `RegisterHandler` ("invited, pending approval") | `Pending` + `Free` | Login **bloqueado** hasta activación admin → **sales-led** |
| `RegisterStudioHandler` ("public self-serve") | `Active` + `Free` | ⚠️ **Activo pero inútil**: la cuota `Free = 0/0/0` lo bloquea igual |

**Infra que YA existe:** estados `Pending/Active/Suspended`, panel admin con **activar** + **cambiar plan**, normalización, etc.
**Lo que NO existe:** concepto de **trial** (fecha de vencimiento) e **integración de pagos**.

→ ⚠️ **Gap a resolver:** el self-serve actual (`RegisterStudioHandler`) deja al usuario "activo pero bloqueado".
Antes de publicar la landing hay que decidir el ruteo del registro (ver Fase A).

---

## 2. Fase A — Sales-led (AHORA, cero código)

**Para 0–~10 clientes. Objetivo: hablar con cada lead y validar el pitch.**

**Flujo:**
1. Landing (P1-3) CTA → **"Solicitá tu prueba"** (formulario simple: nombre, email, estudio).
2. El prospecto se registra por el camino **`RegisterHandler`** → queda en `Pending`.
3. Vos (admin) lo **activás** desde el panel y le ponés **Plan = Pro**. Le mandás credenciales.
4. Usa el sistema completo con **sus extractos reales** (clave para que un contador compre).
5. El "trial" (14–30 días, ver §5) lo **trackeás vos manualmente** (sin código todavía).
6. Al vencer: convertís (queda Pro, cobrás por link/transferencia) o suspendés/bajás desde el panel.

**Por qué A primero:** cero build (reusa activación manual), y el contacto 1-a-1 con los primeros leads
es lo más valioso a esta altura (objeciones, pricing, fricciones).

**✅ Ruteo resuelto (2026-06-08):** el signup público del frontend (`login-page`) ahora llama a
`/api/auth/register` → queda en `Pending` (antes usaba `register-studio` = `Active+Free` roto). Copy
actualizado a "Solicitá tu prueba" + banner de éxito "tu solicitud fue recibida". El endpoint
`register-studio` queda sin uso, reservado para repurposar en la Fase B (trial self-serve).

---

## 3. Fase B — Trial self-serve (PRONTO, build medio)

**Trigger para construirlo:** tras validar el pitch con ~5–10 leads de la Fase A.

**Qué se construye:**
- Campo **`TrialEndsAt: DateTime?`** en el estudio/usuario (StudioOwner).
- `RegisterStudioHandler` (repurposado): crea usuario `Active` + `TrialEndsAt = now + N días`.
- **Cuota lee el trial:** si `TrialEndsAt > now` → aplica límites **Pro**; si venció y `Plan = Free` → bloqueado.
  (Se resuelve **on-request**, no hace falta un job de Hangfire.)
- **Frontend:** banner "Te quedan X días de prueba" + pantalla de bloqueo al vencer con CTA **"Contratar"**.

**Resultado:** landing → registro → 14–30 días full Pro → se bloquea solo → pagar. Escala sin hand-holding.

---

## 4. Fase C — Paywall self-serve con pagos (DESPUÉS, build grande)

**Trigger:** demanda recurrente comprobada (varios pagos manuales/mes ya molestan).

- Integración **MercadoPago** (estándar en Argentina; Stripe tiene soporte AR limitado) + webhooks de
  pago → activación automática y cambio a `Plan = Pro`.
- **No construir antes de tiempo:** con los primeros clientes, cobrar por link/transferencia y activar a mano
  es perfectamente válido y ahorra semanas de integración.

---

## 5. Parámetro a decidir: duración / forma del trial

Un contador trabaja en **ciclos mensuales** (cierres). Un trial de 14 días puede no cubrir un cierre real.

- **Opción recomendada:** atar el trial a **"tu primer cierre gratis"** o **~30 días**, para que el prospecto
  complete una tarea real (un cierre mensual) y vea el valor antes de pagar.
- Alternativa estándar: 14 días (más urgencia, pero quizás no alcanza a cubrir un cierre).

→ **A definir junto con el precio del básico** (sale de COST-01: piso ~$65–85/mes fijo, break-even ~6 clientes).

---

## 6. Resumen de la cadena

```
COST-01 (piso de precio) ─┐
                          ├─► ENTRY-01 (modelo de entrada + precio básico) ─► P1-3 (landing con CTA real)
no hay plan Free ─────────┘
```

- **Hoy:** Fase A (sales-led) operativa ✅ — registro público rutea a `Pending`, admin activa a Pro.
- **Pronto:** Fase B (trial self-serve). **Después:** Fase C (MercadoPago).
