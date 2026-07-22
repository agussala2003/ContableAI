# RUNBOOK · Backup, Retención y Restauración de Base de Datos

> **Alcance:** PostgreSQL de producción de ContableAI.
> **Estado:** Vivo — revisar cada vez que cambie el plan de Neon o el volumen de clientes.
> **Última actualización:** 2026-07-21 (revisión editorial al cierre de la auditoría; creado
> en la fase de hardening SRE — hallazgo B-1).

---

## 0. Contexto real del stack (corrección respecto a la auditoría original)

La auditoría SRE original preguntaba por la estrategia de backups asumiendo "PostgreSQL
gestionado (Render)" en genérico. **Verificado en el código y en `docs/COMMANDS.md` /
`docs/COST-01-unit-economics.md`: el motor de base de datos real es
[Neon](https://neon.tech), no el servicio de Postgres propio de Render.** Render aloja
únicamente el compute de la API (.NET); Neon aloja el dato.

- **Producción:** proyecto Neon "Prod", conectado desde Render vía `ConnectionStrings__DefaultConnection`
  (ver `docs/COMMANDS.md`, sección Neon — no se repite el hostname acá para no duplicar
  superficie de exposición).
- **Desarrollo/staging:** proyecto Neon "Dev" separado (`CONFIG-03` en `BACKLOG.md`), aislado
  del de producción.
- **Plan actual (2026-06-08, `COST-01-unit-economics.md`):** Neon **Free** — `$0/mes`. Esto
  importa porque **el plan gratuito de Neon tiene una ventana de retención de historial (PITR)
  sensiblemente más corta que los planes pagos**, y ese número concreto de días puede cambiar
  con el tiempo. **Antes de fijar el RPO real, verificar en el dashboard de Neon → Settings →
  Backup/Restore cuál es la ventana de retención vigente para el proyecto "Prod"** — no asumir
  el valor de este documento como definitivo sin esa verificación.

Esta distinción importa porque Neon **no funciona como un `pg_dump` nocturno tradicional**:
usa restauración a un punto en el tiempo (PITR) basada en el WAL, dentro de una ventana de
retención configurable por plan. Dentro de esa ventana, el punto de recuperación es
prácticamente continuo (segundos); fuera de ella, el dato es irrecuperable por esta vía.

---

## 1. Política de retención adoptada (fase Beta)

| Ítem | Política |
|---|---|
| **Mecanismo primario** | PITR nativo de Neon (WAL continuo), dentro de la ventana de retención del plan contratado. |
| **Mecanismo secundario (defensa en profundidad)** | `pg_dump` lógico manual/programado, exportado fuera de Neon, antes de cada migración de esquema riesgosa y como mínimo una vez por semana mientras el volumen de clientes sea bajo. Ver §4. |
| **Retención del secundario** | Últimos 4 dumps semanales + el dump previo a cada migración productiva (rotación simple; no requiere infraestructura de retención automatizada mientras el volumen sea bajo). |
| **Alcance** | Solo el proyecto Neon **Prod**. El de Dev no requiere backup (recreable desde `dotnet ef database update` + seed). |
| **Disparador de revisión de esta política** | Cualquiera de los dos disparadores de upgrade ya identificados en `COST-01-unit-economics.md` (Neon Free → Launch por storage, o el primer cliente pago): en ese momento, subir a un plan de Neon con ventana de retención configurable más larga y reevaluar el RPO objetivo de la tabla de abajo. |

---

## 2. RPO y RTO objetivo (fase Beta)

Estos objetivos son deliberadamente conservadores y manuales — corresponden a la etapa
**pre-clientes-pagos / Beta cerrada con "family & friends"** (ver `docs/BACKLOG.md`, Paso 5 del
Go-to-Market), no a un SLA contractual. Se espera endurecerlos antes de facturar a clientes
reales, dado que es un SaaS financiero sujeto a la Ley 25.326.

| Métrica | Objetivo Beta | Justificación |
|---|---|---|
| **RPO (Recovery Point Objective)** | **≤ 24 horas** | Acota la pérdida de datos aceptable a la ventana mínima esperable del plan Free de Neon. Dentro de esa ventana, el PITR real de Neon permite recuperar a un punto casi continuo (mucho mejor que 24h en la práctica) — el número conservador es el peor caso ante un fallo el mismo día que se agote la ventana de retención. |
| **RTO (Recovery Time Objective)** | **≤ 4 horas** | Hoy el proceso de restauración es 100% manual (sin automatización ni runbook probado en producción real — ver §5). 4 horas contempla: detectar el incidente, ejecutar el restore desde la consola de Neon, repuntar la connection string en Render y validar. Aceptable para una Beta sin usuarios pagos con expectativa de disponibilidad 24/7 contractual. |

**Antes de onboardear el primer cliente pago**, estos objetivos deben revisarse a la baja
(RPO cercano a cero vía retención extendida de un plan pago; RTO reducido mediante al menos
un simulacro de restauración documentado, ver §6).

---

## 3. Roles y responsabilidad

- **Dueño de la política:** Agustín (owner del proyecto / `StudioOwner` técnico).
- **Ejecutor del restore:** quien tenga acceso al dashboard de Neon y a las variables de
  entorno de Render en el momento del incidente (hoy, el mismo owner — no hay guardia 24/7 en
  esta fase).
- **Notificación:** no hay canal de alertas automatizado todavía. El primer indicio de un
  incidente de base será `/health/ready` devolviendo `Unhealthy` (ver hardening de health
  checks) o un reporte manual de usuarios de la Beta.

---

## 4. Backup lógico secundario (`pg_dump`) — procedimiento

Complementa al PITR de Neon con una copia fuera de la plataforma, relevante mientras el plan
Free tenga ventana de retención corta.

```bash
# Ejecutar contra el endpoint SIN pooler (operaciones administrativas, no la app).
# Nota: pg_dump usa el formato URI de libpq, NO la connection string estilo .NET de la app.
pg_dump "postgresql://neondb_owner:<pwd>@<host-sin-pooler>.neon.tech/neondb?sslmode=verify-full" \
  --format=custom \
  --file="contableai-prod-$(date +%Y%m%d).dump"

# Guardar el archivo en un storage fuera de Neon/Render (ej. un bucket privado o disco cifrado
# local del owner). NO commitear el dump al repositorio ni dejarlo en un directorio sin cifrar.
```

- **Cuándo correrlo:** antes de cualquier `dotnet ef database update` contra Prod (ver
  `docs/COMMANDS.md`, sección de migraciones), y como mínimo una vez por semana mientras dure
  la fase Beta.
- **Verificación mínima:** tras generarlo, confirmar que el archivo pesa lo esperable (no 0
  bytes) y que `pg_restore --list` sobre el dump lista las tablas del esquema sin error.

---

## 5. Procedimiento de restauración ante desastre (teórico/documental)

> ⚠️ Este procedimiento **no fue ensayado aún contra un entorno real** (ver §6, acción
> pendiente). Es la secuencia esperada según la documentación de Neon y la arquitectura del
> repo; validar el primer simulacro antes de confiar en los tiempos del RTO de §2.

1. **Confirmar el incidente.** `/health/ready` en `Unhealthy` + error de conexión reproducible
   (descartar que sea un problema de Render/red antes de asumir pérdida de datos en Neon).
2. **Congelar escritura si es posible.** Si el incidente es corrupción de datos (no caída de
   infraestructura), pausar el tráfico de escritura: escalar el servicio de Render a 0
   instancias o poner el frontend en modo mantenimiento, para no seguir escribiendo sobre un
   estado dañado mientras se decide el punto de restauración.
3. **Elegir el punto de restauración.** Desde el dashboard de Neon → proyecto "Prod" →
   *Restore* → seleccionar timestamp objetivo (el más reciente posible dentro de la ventana de
   retención, anterior al incidente).
4. **Restaurar.** Neon crea una rama (branch) nueva con el estado de ese punto en el tiempo
   (no sobrescribe el proyecto original in-place, lo que da una red de seguridad: se puede
   comparar la rama restaurada contra la actual antes de cortar tráfico hacia ella).
5. **Validar la rama restaurada.** Contra la connection string de la rama nueva, correr un
   smoke test mínimo: `dotnet ef database update --dry-run`/verificar versión de migración
   aplicada, y una query de sanity (conteo de `BankTransactions`, `Companies`) comparado contra
   lo esperado antes del incidente.
6. **Repuntar producción.** Actualizar `ConnectionStrings__DefaultConnection` en las variables
   de entorno de Render (o promover la rama restaurada a primaria, según la opción que ofrezca
   Neon en ese momento) y redeployar.
7. **Verificar salud post-restore.** `/health/live`, `/health/ready`, y un login + carga de
   dashboard real desde el frontend.
8. **Si hubo backup lógico más reciente que el punto de PITR elegido** (caso: la ventana de
   retención ya expiró para el momento del incidente), restaurar desde el último `pg_dump` de
   §4 con `pg_restore` sobre una base nueva, aceptando la pérdida de datos entre ese dump y el
   incidente (documentar esa brecha en el post-mortem).
9. **Post-mortem.** Documentar causa raíz, tiempo real de RTO/RPO efectivo, y si corresponde,
   ajustar esta política.

---

## 6. Acciones pendientes (tareas operativas en el entorno productivo de Neon)

- [ ] **Verificar en el dashboard de Neon la ventana de retención exacta vigente** para el
  proyecto Prod (plan Free) y ajustar el RPO de §2 si es distinto a 24h.
- [ ] **Simulacro de restauración real** (§5) al menos una vez, contra un branch de prueba de
  Neon — un backup no verificado equivale a no tener backup.
- [ ] **Automatizar el `pg_dump` semanal** de §4 (hoy es manual) una vez que el volumen de
  clientes lo justifique.
- [ ] **Reevaluar RPO/RTO** al primer cliente pago o al upgrade de plan de Neon (ver
  disparadores en `COST-01-unit-economics.md`).
