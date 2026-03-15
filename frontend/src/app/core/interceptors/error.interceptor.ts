import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { ToastService } from '../services/toast.service';

interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  message?: string;
  errors?: Record<string, string[] | string>;
}

/**
 * Interceptor global de errores HTTP.
 * - 0 (sin conexión)   → toast "Sin conexión"
 * - 401 (expirado)     → cierra sesión y navega a /login
 * - 403 (sin permisos) → toast + no redirige
 * - 500+               → toast genérico de servidor
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const auth   = inject(AuthService);
  const toast  = inject(ToastService);

  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      // Red / CORS / sin servidor → status 0
      if (error.status === 0) {
        toast.show('Sin conexión con el servidor. Verificá que el backend esté corriendo.', 'error');
        return throwError(() => error);
      }

      // Token expirado o inválido
      if (error.status === 401) {
        // No disparar en el endpoint de login (evita loop)
        if (!req.url.includes('/auth/login') && !req.url.includes('/auth/register-studio')) {
          auth.logout();
          toast.show('Tu sesión expiró. Ingresá nuevamente.', 'warning');
        }
        return throwError(() => error);
      }

      if (error.status === 400) {
        const payload = (error.error ?? {}) as ProblemDetails;
        const validationErrors = payload.errors;

        if (validationErrors && typeof validationErrors === 'object') {
          const lines = Object.entries(validationErrors)
            .flatMap(([field, messages]) => {
              const list = Array.isArray(messages) ? messages : [messages];
              return list.filter(Boolean).map(msg => `${field}: ${msg}`);
            })
            .slice(0, 6);

          const validationMessage = lines.length > 0
            ? `Errores de validación: ${lines.join(' | ')}`
            : (payload.detail ?? payload.title ?? 'Error de validación.');

          toast.show(validationMessage, 'warning');
          return throwError(() => error);
        }

        const fallback400 = payload.detail ?? payload.title ?? payload.message ?? 'Solicitud inválida.';
        toast.show(fallback400, 'warning');
        return throwError(() => error);
      }

      // Sin permisos suficientes
      if (error.status === 403) {
        toast.show('No tenés permisos para realizar esta acción.', 'error');
        return throwError(() => error);
      }

      if (error.status === 404) {
        toast.show('Recurso no encontrado.', 'warning');
        return throwError(() => error);
      }

      // Quota excedida
      if (error.status === 402) {
        toast.show('Límite del plan alcanzado. Actualizá tu suscripción en la sección Plan.', 'warning');
        return throwError(() => error);
      }

      // Errores de servidor (500+)
      if (error.status >= 500) {
        const payload = (error.error ?? {}) as ProblemDetails;
        const msg = payload.detail ?? payload.title ?? payload.message ?? 'Ocurrió un error inesperado, intentá de nuevo.';
        toast.show(msg, 'error');
        return throwError(() => error);
      }

      // Otros errores (400, 404, etc.) los maneja cada componente
      return throwError(() => error);
    }),
  );
};
