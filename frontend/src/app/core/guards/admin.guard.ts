import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

/** Permite el acceso solo a usuarios con rol SystemAdmin. */
export const adminGuard: CanActivateFn = () => {
  const auth   = inject(AuthService);
  const router = inject(Router);

  const user = auth.currentUser();
  if (auth.isLoggedIn() && auth.isSystemAdmin()) {
    return true;
  }

  return router.createUrlTree([user ? '/' : '/login']);
};
