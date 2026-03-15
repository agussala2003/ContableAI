import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map, tap } from 'rxjs';
import { Router } from '@angular/router';
import { ConfigService } from '../config/config.service';

export interface AuthUser {
  id: string;
  email: string;
  displayName: string;
  role: string;
  studioTenantId: string;
}

export interface AuthResponse {
  token: string;
  user: AuthUser;
}

interface BackendAuthResponse {
  token: string;
  userId?: string;
  email?: string;
  displayName?: string;
  role?: string;
  studioTenantId?: string;
}

export interface RegisterPendingResponse {
  pendingApproval: boolean;
  message: string;
}

export interface RegisterStudioRequest {
  studioName: string;
  email: string;
  password: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  email: string;
  password: string;
  displayName?: string;
  studioTenantId?: string; // omitir para crear un estudio nuevo
}

const TOKEN_KEY = 'contableai_token';
const USER_KEY  = 'contableai_user';
// TODO: Mover a HttpOnly cookies por seguridad XSS.

@Injectable({ providedIn: 'root' })
export class AuthService {
  private http   = inject(HttpClient);
  private router = inject(Router);
  private configService = inject(ConfigService);

  private get baseUrl(): string {
    return `${this.configService.config().apiUrl}/auth`;
  }

  /** Usuario actualmente autenticado (reactivo). */
  currentUser = signal<AuthUser | null>(this.loadUserOrToken());

  private loadUserOrToken(): AuthUser | null {
    const stored = localStorage.getItem(USER_KEY);
    if (stored && stored !== 'undefined' && stored !== 'null') {
      try {
        return JSON.parse(stored);
      } catch {
        localStorage.removeItem(USER_KEY);
      }
    }

    const token = this.getToken();
    const user = token ? this.userFromToken(token) : null;
    if (user) {
      localStorage.setItem(USER_KEY, JSON.stringify(user));
    }
    return user;
  }

  getToken(): string | null {
    return localStorage.getItem(TOKEN_KEY);
  }

  isLoggedIn(): boolean {
    const token = this.getToken();
    if (!token) return false;
    try {
      const payload = JSON.parse(atob(token.split('.')[1]));
      return payload.exp * 1000 > Date.now();
    } catch {
      return false;
    }
  }

  login(req: LoginRequest): Observable<AuthResponse> {
    return this.http.post<BackendAuthResponse>(`${this.baseUrl}/login`, req).pipe(
      map(res => this.mapBackendAuthResponse(res)),
      tap(res => this.storeSession(res)),
    );
  }

  register(req: RegisterRequest): Observable<RegisterPendingResponse> {
    return this.http.post<RegisterPendingResponse>(`${this.baseUrl}/register`, req);
    // No almacena sesión — la cuenta queda pendiente de activación manual.
  }

  /** Registro público de estudio nuevo: cuenta activa y auto-login immediato. */
  registerStudio(req: RegisterStudioRequest): Observable<AuthResponse> {
    return this.http.post<BackendAuthResponse>(`${this.baseUrl}/register-studio`, req).pipe(
      map(res => this.mapBackendAuthResponse(res)),
      tap(res => this.storeSession(res)),
    );
  }

  logout(): void {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    this.currentUser.set(null);
    this.router.navigate(['/login']);
  }

  private storeSession(res: AuthResponse): void {
    localStorage.setItem(TOKEN_KEY, res.token);
    localStorage.setItem(USER_KEY, JSON.stringify(res.user));
    this.currentUser.set(res.user);
  }

  forgotPassword(email: string): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(`${this.baseUrl}/forgot-password`, { email });
  }

  resetPassword(token: string, email: string, newPassword: string): Observable<{ message: string }> {
    return this.http.post<{ message: string }>(`${this.baseUrl}/reset-password`, { token, email, newPassword });
  }

  private normalizedRole(role?: string | null): string {
    return (role ?? '').toLowerCase().replace(/[^a-z]/g, '');
  }

  isSystemAdmin(): boolean {
    const role = this.normalizedRole(this.currentUser()?.role);
    return role === 'systemadmin' || role === 'admin';
  }

  isStudioOwnerOrAdmin(): boolean {
    const role = this.normalizedRole(this.currentUser()?.role);
    return role === 'studioowner' || role === 'systemadmin' || role === 'admin';
  }

  private mapBackendAuthResponse(res: BackendAuthResponse): AuthResponse {
    const tokenUser = this.userFromToken(res.token);

    const user: AuthUser = {
      id: res.userId ?? tokenUser?.id ?? '',
      email: res.email ?? tokenUser?.email ?? '',
      displayName: res.displayName ?? tokenUser?.displayName ?? '',
      role: res.role ?? tokenUser?.role ?? 'DataEntry',
      studioTenantId: res.studioTenantId ?? tokenUser?.studioTenantId ?? '',
    };

    return { token: res.token, user };
  }

  private userFromToken(token: string): AuthUser | null {
    try {
      const payload = JSON.parse(atob(token.split('.')[1])) as Record<string, unknown>;
      const role = (payload['role'] as string)
        || (payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] as string)
        || '';

      return {
        id: String(payload['sub'] ?? ''),
        email: String(payload['email'] ?? ''),
        displayName: String(payload['displayName'] ?? payload['name'] ?? ''),
        role,
        studioTenantId: String(payload['studioTenantId'] ?? ''),
      };
    } catch {
      return null;
    }
  }
}
