import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

/**
 * M-4: permite el acceso solo al titular del estudio (StudioOwner) o al SystemAdmin.
 * Protege las secciones de gestión avanzada (reglas de estudio, etc.). El rol operativo
 * DataEntry es redirigido al inicio.
 */
export const ownerGuard: CanActivateFn = () => {
  const auth   = inject(AuthService);
  const router = inject(Router);

  if (auth.isLoggedIn() && auth.isStudioOwnerOrAdmin()) {
    return true;
  }

  return router.createUrlTree(['/']);
};
