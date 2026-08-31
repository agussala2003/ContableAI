import { HttpContextToken, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { finalize } from 'rxjs';
import { LoadingService } from '../services/loading.service';

export const SKIP_LOADING = new HttpContextToken<boolean>(() => false);

export const loadingInterceptor: HttpInterceptorFn = (req, next) => {
  const loadingService = inject(LoadingService);

  if (req.context.get(SKIP_LOADING)) {
    return next(req);
  }

  // El token se cierra en finalize, que corre en las TRES salidas posibles: respuesta, error y
  // cancelación (unsubscribe, ej. el switchMap de la grilla descartando una carga anterior).
  const token = loadingService.begin();

  return next(req).pipe(
    finalize(() => loadingService.end(token)),
  );
};
