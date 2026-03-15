# Anotaciones Post-Beta

## Backlog producto
- [ ] Crear pagina para administracion de cuentas contables.
- [ ] Crear pagina para planes, consumo y limites del estudio.

## Caso a resolver: re-subida de extracto ya procesado

### Problema
Si un usuario sube un extracto, genera asientos y luego vuelve a subir el mismo archivo para reclasificar cuentas, hoy no hay una politica clara para:
- detectar que es el mismo extracto,
- decidir si se recalcula,
- y proteger la integridad de asientos ya generados.

### Objetivo
Permitir correcciones sin romper la trazabilidad contable.

### Propuesta funcional (version recomendada)
1. Guardar una huella del extracto al subirlo.
- hash del archivo original,
- cantidad de movimientos,
- rango de fechas,
- total debitos y creditos.

2. Al detectar posible duplicado, mostrar decision al usuario.
- Mantener version existente (no hacer nada).
- Crear nueva version del extracto (recomendado).
- Reemplazar version anterior (solo si no hay asientos bloqueados).

3. Versionado en lugar de sobreescritura directa.
- Extracto v1, v2, v3...
- Solo una version activa para conciliacion.
- Historial visible para auditoria.

4. Regla de seguridad para asientos.
- Si la version anterior ya genero asientos, no borrar en cascada automatico.
- Marcar esos asientos como potencialmente desactualizados.
- Ofrecer accion guiada: Recalcular asientos con previsualizacion de impacto.

5. Confirmacion explicita antes de cambios destructivos.
- Mostrar cuantos movimientos y asientos se veran afectados.
- Pedir confirmacion final.

## Criterios de negocio sugeridos
- No eliminar asientos automaticamente si estan publicados/cerrados.
- Permitir reemplazo automatico solo en estado borrador.
- Registrar evento de auditoria por cada re-subida y recalculo.

## UX minima sugerida
- Modal de duplicado detectado con opciones claras.
- Badge en movimientos: Version actual / Version anterior.
- Vista de diferencias antes de aplicar reemplazo.

## Definicion de terminado (MVP)
- Deteccion de duplicado por hash + metadatos.
- Flujo de decision de usuario.
- Versionado de extractos.
- Recalculo controlado de asientos en borrador.
- Auditoria basica de acciones.
