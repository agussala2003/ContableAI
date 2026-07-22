import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map, tap, finalize, shareReplay, catchError, of } from 'rxjs';
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

/**
 * A-3: el JWT de acceso (vida corta) vive SOLO en memoria — nunca en localStorage —
 * para que un XSS no pueda robar una sesión persistente. El refresh token viaja en una
 * cookie HttpOnly (inaccesible a JS) y se usa vía /auth/refresh (withCredentials) para
 * rehidratar la sesión al cargar la app o cuando el access token expira.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private http   = inject(HttpClient);
  private router = inject(Router);
  private configService = inject(ConfigService);

  /** JWT de acceso en memoria (no persistido). */
  private accessToken: string | null = null;

  /** Refresh en vuelo compartido para deduplicar llamadas concurrentes. */
  private refreshInFlight$?: Observable<string>;

  private get baseUrl(): string {
    return `${this.configService.config().apiUrl}/auth`;
  }

  /** Usuario actualmente autenticado (reactivo). */
  currentUser = signal<AuthUser | null>(null);

  getToken(): string | null {
    return this.accessToken;
  }

  isLoggedIn(): boolean {
    const token = this.accessToken;
    if (!token) return false;
    try {
      const payload = JSON.parse(atob(token.split('.')[1]));
      return payload.exp * 1000 > Date.now();
    } catch {
      return false;
    }
  }

  login(req: LoginRequest): Observable<AuthResponse> {
    return this.http
      .post<BackendAuthResponse>(`${this.baseUrl}/login`, req, { withCredentials: true })
      .pipe(
        map(res => this.mapBackendAuthResponse(res)),
        tap(res => this.storeSession(res)),
      );
  }

  register(req: RegisterRequest): Observable<RegisterPendingResponse> {
    return this.http.post<RegisterPendingResponse>(`${this.baseUrl}/register`, req);
    // No almacena sesión — la cuenta queda pendiente de activación manual.
  }

  /** Registro público de estudio nuevo: cuenta activa y auto-login inmediato. */
  registerStudio(req: RegisterStudioRequest): Observable<AuthResponse> {
    return this.http
      .post<BackendAuthResponse>(`${this.baseUrl}/register-studio`, req, { withCredentials: true })
      .pipe(
        map(res => this.mapBackendAuthResponse(res)),
        tap(res => this.storeSession(res)),
      );
  }

  /**
   * Renueva el JWT de acceso usando el refresh token (cookie HttpOnly). Deduplica llamadas
   * concurrentes: varias requests que reciban 401 a la vez comparten un único /auth/refresh.
   */
  refresh(): Observable<string> {
    if (!this.refreshInFlight$) {
      this.refreshInFlight$ = this.http
        .post<BackendAuthResponse>(`${this.baseUrl}/refresh`, {}, { withCredentials: true })
        .pipe(
          map(res => this.mapBackendAuthResponse(res)),
          tap(res => this.storeSession(res)),
          map(res => res.token),
          finalize(() => { this.refreshInFlight$ = undefined; }),
          shareReplay(1),
        );
    }
    return this.refreshInFlight$;
  }

  /**
   * Intento silencioso de rehidratar la sesión al arrancar la app (APP_INITIALIZER).
   * Si no hay cookie válida, resuelve como anónimo sin propagar error.
   */
  restoreSession(): Observable<boolean> {
    return this.refresh().pipe(
      map(() => true),
      catchError(() => {
        this.clearSession();
        return of(false);
      }),
    );
  }

  logout(): void {
    // Revoca el refresh token en el server (best-effort) y limpia el estado local.
    this.http.post(`${this.baseUrl}/logout`, {}, { withCredentials: true }).pipe(
      catchError(() => of(null)),
    ).subscribe(() => {
      this.clearSession();
      this.router.navigate(['/login']);
    });
  }

  private storeSession(res: AuthResponse): void {
    this.accessToken = res.token;
    this.currentUser.set(res.user);
  }

  private clearSession(): void {
    this.accessToken = null;
    this.currentUser.set(null);
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
