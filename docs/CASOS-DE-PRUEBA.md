# Casos de Prueba — ContableAI

## Prerequisitos antes de probar

1. **Backend corriendo** (Terminal dotnet):
   ```
   cd backend && dotnet run --project src\ContableAI.API
   ```

2. **Frontend corriendo** (Terminal esbuild):
   ```
   cd frontend && ng s
   ```

3. **Qdrant corriendo** (solo para F4-3):
   ```
   docker compose up qdrant -d
   ```

---

## F3-3 — Badge "Requiere desglose" (pagos de tarjeta)

**Archivo:** `docs/test-f3-3-tarjeta.csv`  
**Banco a seleccionar:** BBVA (o el banco genérico que acepta ese formato)

**Qué hace:**  
Cuando una transacción contiene keywords como `PAGO TARJETA`, `PAGO VISA`, `PAGO MASTER`,
`PAGO AMEX`, `PAGO AUTOMATICO VISA`, etc., el sistema la categoriza y además pone
un badge rojo **"Requiere desglose"** en la columna de descripción.

**Cómo probar:**
1. Ir a Transacciones → Subir CSV
2. Seleccionar banco `BBVA`, subir `test-f3-3-tarjeta.csv`
3. Verificar que las filas con `PAGO TARJETA VISA`, `PAGO MASTER CARD`,
   `PAGO AUTOMATICO VISA` y `PAGO AMEX CORPORATE` muestran el badge rojo.

**Resultado esperado:**  
- 4 de las 8 filas deben mostrar el badge rojo "Requiere desglose" (las que contienen keywords de tarjeta).
- Las otras 4 (transferencia, acreditación, compra insumos, honorarios) no tienen badge.

---

## F3-2 — Badge "Posible duplicado" (pagos sindicales)

**Archivo:** `docs/test-f3-2-duplicados.csv`  
**Banco a seleccionar:** BBVA

**Qué hace:**  
Detecta cuando el mismo importe sindical aparece dos veces en el archivo con fechas
cercanas (±2 días). Brands: UTGRA, UOCRA, FATSA, SMATA, UOM, UPCN, SINDICATO,
CUOTA SINDICAL, CUOTA GREMIAL, APORTE SINDICAL.

**Cómo probar:**
1. Ir a Transacciones → Subir CSV
2. Seleccionar banco `BBVA`, subir `test-f3-2-duplicados.csv`
3. El CSV tiene dos pares de duplicados:
   - Dos filas de UTGRA por $18.750 el mismo día (líneas 4 y 5 en el CSV)
   - Dos filas de UOCRA por $12.500 con 2 días de diferencia (líneas 7 y 9)

**Resultado esperado:**  
- Las filas duplicadas deben mostrar el badge amarillo **"Posible duplicado"**.
- Las otras transacciones no tienen badge.

---

## F2-2 — Parser MercadoPago (filtro de comisiones)

**Archivo:** `docs/test-f2-2-mercadopago.csv`  
**Banco a seleccionar:** MERCADOPAGO

**Qué hace:**  
Parsea el CSV de MercadoPago (delimitado con `;`, columna `Tipo de operación`)
y **filtra automáticamente** las filas de Comisión e Impuesto para que no aparezcan
como gastos duplicados.

**Cómo probar:**
1. Ir a Transacciones → Subir CSV
2. Seleccionar banco `MERCADOPAGO`, subir `test-f2-2-mercadopago.csv`
3. El CSV tiene 10 filas: 6 operaciones reales + 4 comisiones/impuestos

**Resultado esperado:**  
- Solo se importan **6 transacciones** (las de tipo Pago, Retiro, Compra).
- Las 4 filas de Comisión e Impuesto son descartadas silenciosamente.

---

## F2-3 — Parser Ualá (detección débito/crédito)

**Archivo:** `docs/test-f2-3-uala.csv`  
**Banco a seleccionar:** UALA

**Qué hace:**  
Parsea el CSV de Ualá (delimitado con `;`, fecha en formato `yyyy-MM-dd`,
columna `Tipo de movimiento` con valores "Crédito"/"Débito").

**Cómo probar:**
1. Ir a Transacciones → Subir CSV
2. Seleccionar banco `UALA`, subir `test-f2-3-uala.csv`
3. El CSV tiene 8 filas con tipos explicitados

**Resultado esperado:**  
- 3 transacciones de tipo **Crédito** (transferencias recibidas, cobros)
- 5 transacciones de tipo **Débito** (pagos, compras, transferencias enviadas)
- Las fechas se parsean correctamente desde formato `2026-02-XX`

---

## F4-3 — Memoria vectorial Qdrant (aprendizaje por corrección)

> **Requiere Qdrant corriendo:** `docker compose up qdrant -d`

**Qué hace:**  
Cuando corregís manualmente la cuenta de una transacción (vía el botón de edición
en la grilla), el par descripción→cuenta se guarda en Qdrant. La próxima vez que
se sube una transacción con descripción similar, el sistema usa ese recuerdo
en lugar de llamar al LLM, y el origen aparece como **"Memoria"** (badge teal).

**Cómo probar:**

### Paso 1 — Subir una transacción y corregirla
1. Subir cualquier CSV con una fila de descripción conocida, ej.:
   ```
   Fecha,Descripcion,Importe
   25/02/2026,ALQUILER LOCAL PALERMO,-120000.00
   ```
2. La IA probablemente la clasifica como alguna cuenta genérica.
3. Corregirla manualmente: click en el lápiz → asignar `5.1.1.1 Alquileres` → Guardar.

### Paso 2 — Subir la misma descripción de nuevo
1. Subir otro CSV con una descripción similar:
   ```
   Fecha,Descripcion,Importe
   26/02/2026,ALQUILER LOCAL PALERMO MARZO,-120000.00
   ```
2. El sistema busca en Qdrant antes de llamar al LLM.
3. Si la similitud ≥ 85%, asigna `5.1.1.1 Alquileres` directamente.

**Resultado esperado:**  
- La columna **Origen** muestra el badge teal **"Memoria"** en lugar de "IA".
- No se realiza llamada al LLM (sin costos de API).
- Los logs del backend muestran la ruta de Qdrant, no la de OpenAI.

> **Nota sobre embeddings en dev:**  
> En entorno local se usa un embedding determinístico basado en hash (sin API key).  
> Funciona perfecto para textos idénticos o muy similares.  
> Para similitud semántica real (ej. "alquiler" ≈ "locación"), se necesita una  
> API key de OpenAI con créditos configurada en `appsettings.Development.json`.

---

## Resumen de archivos de prueba

| Archivo | Feature | Banco | Filas | Observación |
|---|---|---|---|---|
| `test-f3-3-tarjeta.csv` | F3-3 | BBVA | 8 | 4 con badge rojo |
| `test-f3-2-duplicados.csv` | F3-2 | BBVA | 9 | 2 pares de duplicados |
| `test-f2-2-mercadopago.csv` | F2-2 | MERCADOPAGO | 10 | 4 comisiones filtradas |
| `test-f2-3-uala.csv` | F2-3 | UALA | 8 | Fechas yyyy-MM-dd |
