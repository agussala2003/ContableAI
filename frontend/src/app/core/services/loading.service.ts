import { Injectable, computed, signal } from '@angular/core';

/**
 * Requests en vuelo que levantan el overlay global bloqueante del layout.
 *
 * Antes era un contador (`pendingRequests++/--`). El problema de un contador es que una sola
 * request que nunca resuelve —ni completa, ni falla, ni se cancela— lo deja en 1 para siempre y
 * el overlay tapa la app entera sin forma de salir salvo recargar. Es exactamente lo que pasaba
 * al reaplicar una regla: el job terminaba, el toast aparecía, y el GET de la grilla que venía
 * detrás quedaba colgado dejando la pantalla bloqueada.
 *
 * Ahora cada request tiene su propio token y dos garantías:
 *  - `end()` es idempotente: cerrar dos veces el mismo token no descuenta de más ni de menos.
 *  - un watchdog libera el token solo si la request supera {@link MAX_REQUEST_MS}. El overlay
 *    es BLOQUEANTE: dejarlo indefinido convierte un problema de red en una app inutilizable.
 *    El warning en consola deja el problema visible en vez de taparlo.
 */
@Injectable({ providedIn: 'root' })
export class LoadingService {
  /** Tope de bloqueo. Por encima de esto la request sigue viva, pero deja de tapar la pantalla. */
  private static readonly MAX_REQUEST_MS = 30_000;

  private readonly pending = signal<ReadonlySet<number>>(new Set());
  private readonly watchdogs = new Map<number, ReturnType<typeof setTimeout>>();
  private nextToken = 0;

  readonly isLoading = computed(() => this.pending().size > 0);

  /** Registra una request en vuelo. Devuelve el token con el que hay que cerrarla. */
  begin(): number {
    const token = this.nextToken++;
    this.pending.update(set => new Set(set).add(token));

    this.watchdogs.set(token, setTimeout(() => {
      if (!this.pending().has(token)) return;
      console.warn(
        `[LoadingService] Request #${token} superó ${LoadingService.MAX_REQUEST_MS} ms sin resolver. ` +
        'Se libera el overlay para no dejar la pantalla bloqueada.',
      );
      this.release(token);
    }, LoadingService.MAX_REQUEST_MS));

    return token;
  }

  /** Cierra una request. Idempotente: llamarla dos veces con el mismo token no hace nada. */
  end(token: number): void {
    this.release(token);
  }

  /** Libera todo (ej: logout). */
  reset(): void {
    for (const timer of this.watchdogs.values()) clearTimeout(timer);
    this.watchdogs.clear();
    this.pending.set(new Set());
  }

  private release(token: number): void {
    const timer = this.watchdogs.get(token);
    if (timer !== undefined) {
      clearTimeout(timer);
      this.watchdogs.delete(token);
    }

    this.pending.update(set => {
      if (!set.has(token)) return set;   // ya cerrada: no toca el signal, no re-renderiza
      const next = new Set(set);
      next.delete(token);
      return next;
    });
  }
}
