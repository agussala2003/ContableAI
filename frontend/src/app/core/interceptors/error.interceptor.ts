import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';
import { ToastService } from '../services/toast.service';

interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  message?: string;
  errors?: Record<string, string[] | string>;
  // O-3: correlation id que el backend inyecta en el ProblemDetails para correlacionar
  // el error con los logs del servidor. Puede venir en la raíz o dentro de `extensions`.
  traceId?: string;
  extensions?: { traceId?: string };
}

/**
 * Etiquetas en castellano para los campos que el backend devuelve en los errores
 * de validación (nombres PascalCase en inglés que el contador no tiene por qué conocer).
 */
const FIELD_LABELS: Record<string, string> = {
  email: 'Email',
  password: 'Contraseña',
  displayname: 'Nombre del estudio',
  name: 'Nombre',
  cuit: 'CUIT',
  bankaccountname: 'Cuenta bancaria',
  keyword: 'Palabra clave',
  targetaccount: 'Cuenta contable',
  priority: 'Prioridad',
  direction: 'Dirección',
  companyid: 'Empresa',
  bankcode: 'Banco',
  file: 'Archivo',
  files: 'Archivos',
  amount: 'Importe',
  date: 'Fecha',
  description: 'Descripción',
  month: 'Mes',
  year: 'Año',
  currency: 'Moneda',
  token: 'Enlace de recuperación',
};

/** Traduce el nombre técnico del campo; si no está mapeado, separa el PascalCase en palabras. */
function fieldLabel(field: string): string {
  const known = FIELD_LABELS[field.toLowerCase()];
  if (known) return known;
  const spaced = field.replace(/([a-z0-9])([A-Z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
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
        toast.show('No pudimos conectar con el servidor. Revisá tu conexión a internet e intentá de nuevo.', 'error');
        return throwError(() => error);
      }

      // Token expirado o inválido (A-3)
      if (error.status === 401) {
        const isAuthCall     = req.url.includes('/auth/');
        const alreadyRetried = req.headers.has('X-Auth-Retry');

        // Requests normales a la API: intentar UN silent-refresh y reintentar la original
        // con el nuevo access token. Los endpoints /auth (login/refresh/logout) no se reintentan.
        if (!isAuthCall && !alreadyRetried) {
          return auth.refresh().pipe(
            switchMap(newToken =>
              next(req.clone({
                setHeaders: { Authorization: `Bearer ${newToken}`, 'X-Auth-Retry': '1' },
              })),
            ),
            catchError(() => {
              // El refresh falló (cookie inválida/expirada/revocada) → cerrar sesión.
              auth.logout();
              toast.show('Tu sesión expiró. Ingresá nuevamente.', 'warning');
              return throwError(() => error);
            }),
          );
        }

        // Ya se reintentó una request de API y volvió a fallar → cerrar sesión.
        if (!isAuthCall && alreadyRetried) {
          auth.logout();
          toast.show('Tu sesión expiró. Ingresá nuevamente.', 'warning');
        }
        // Los 401 de /auth/* (login, refresh en arranque anónimo, etc.) se propagan sin efectos:
        // los maneja cada caller (componente de login / restoreSession del APP_INITIALIZER).
        return throwError(() => error);
      }

      if (error.status === 400) {
        const payload = (error.error ?? {}) as ProblemDetails;
        const validationErrors = payload.errors;

        if (validationErrors && typeof validationErrors === 'object') {
          const lines = Object.entries(validationErrors)
            .flatMap(([field, messages]) => {
              const list = Array.isArray(messages) ? messages : [messages];
              return list.filter(Boolean).map(msg => `${fieldLabel(field)}: ${msg}`);
            })
            .slice(0, 6);

          const validationMessage = lines.length > 0
            ? `Revisá estos datos antes de continuar — ${lines.join(' | ')}`
            : (payload.detail ?? payload.title ?? 'Algunos datos no son válidos. Revisalos e intentá de nuevo.');

          toast.show(validationMessage, 'warning');
          return throwError(() => error);
        }

        const fallback400 = payload.detail ?? payload.title ?? payload.message
          ?? 'No pudimos procesar la solicitud. Revisá los datos ingresados e intentá de nuevo.';
        toast.show(fallback400, 'warning');
        return throwError(() => error);
      }

      // Sin permisos suficientes
      if (error.status === 403) {
        toast.show('No tenés permisos para realizar esta acción.', 'error');
        return throwError(() => error);
      }

      if (error.status === 404) {
        toast.show('No encontramos lo que estabas buscando. Puede que se haya eliminado — actualizá la página e intentá de nuevo.', 'warning');
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
        const baseMsg = payload.detail ?? payload.title ?? payload.message
          ?? 'Tuvimos un problema procesando tu pedido. Intentá de nuevo en unos minutos; si el problema sigue, contactá a soporte.';
        // O-3: si el backend adjuntó un traceId, mostrarlo para que el usuario pueda
        // reportarlo a soporte y correlacionarlo con los logs del servidor.
        const traceId = payload.traceId ?? payload.extensions?.traceId;
        const msg = traceId ? `${baseMsg} (Código para soporte: ${traceId})` : baseMsg;
        toast.show(msg, 'error');
        return throwError(() => error);
      }

      // Otros errores (400, 404, etc.) los maneja cada componente
      return throwError(() => error);
    }),
  );
};
