import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';
import { adminGuard } from './core/guards/admin.guard';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/auth/pages/login-page/login-page').then(m => m.LoginPage),
  },
  {
    path: 'forgot-password',
    loadComponent: () => import('./features/auth/pages/forgot-password-page/forgot-password-page').then(m => m.ForgotPasswordPage),
  },
  {
    path: 'reset-password',
    loadComponent: () => import('./features/auth/pages/reset-password-page/reset-password-page').then(m => m.ResetPasswordPage),
  },
  {
    path: '',
    loadComponent: () => import('./core/layout/main-layout/main-layout').then(m => m.MainLayout),
    canActivate: [authGuard],
    children: [
      {
        path: '',
        loadComponent: () => import('./features/reconciliation/pages/reconciliation-page/reconciliation-page').then(m => m.ReconciliationPage),
      },
      {
        path: 'rules',
        loadComponent: () => import('./features/rules/pages/rules-page/rules-page').then(m => m.RulesPage),
      },
      {
        path: 'admin',
        loadComponent: () => import('./features/admin/pages/admin-page/admin-page').then(m => m.AdminPage),
        canActivate: [adminGuard],
      },
      {
        path: 'journal',
        loadComponent: () => import('./features/journal/pages/journal-page/journal-page').then(m => m.JournalPage),
      },
    ],
  },
];
