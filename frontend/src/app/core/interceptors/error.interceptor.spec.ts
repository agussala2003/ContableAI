import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { errorInterceptor } from './error.interceptor';
import { AuthService } from '../services/auth.service';
import { ToastService } from '../services/toast.service';

/**
 * Tests del flujo crítico de sesión del interceptor global de errores: ante un 401, intenta
 * UN silent-refresh y reintenta la request original con el token nuevo; si el refresh falla
 * (o la request ya había sido reintentada), desloguea al usuario. Usa HttpTestingController
 * para simular las respuestas del backend en cada paso de la cadena.
 */
describe('errorInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let authService: AuthService;
  let toast: ToastService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]), // AuthService.logout() navega a /login
      ],
    });

    http        = TestBed.inject(HttpClient);
    httpMock    = TestBed.inject(HttpTestingController);
    authService = TestBed.inject(AuthService);
    toast       = TestBed.inject(ToastService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('ante un 401, intenta un silent-refresh y reintenta la request original con el token nuevo', () => {
    let received: unknown;
    let errored: unknown;

    http.get('/api/protected').subscribe({
      next:  v => (received = v),
      error: e => (errored = e),
    });

    httpMock.expectOne(r => r.method === 'GET' && r.url === '/api/protected')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    const refreshReq = httpMock.expectOne(r => r.method === 'POST' && r.url.endsWith('/auth/refresh'));
    expect(refreshReq.request.withCredentials).toBe(true);
    refreshReq.flush({
      token: 'fake.token.value',
      userId: 'u1', email: 'a@a.com', displayName: 'A', role: 'DataEntry', studioTenantId: 'ST1',
    });

    const retried = httpMock.expectOne(
      r => r.method === 'GET' && r.url === '/api/protected' && r.headers.has('X-Auth-Retry'),
    );
    expect(retried.request.headers.get('Authorization')).toBe('Bearer fake.token.value');
    retried.flush({ ok: true });

    expect(received).toEqual({ ok: true });
    expect(errored).toBeUndefined();
  });

  it('si el refresh falla, desloguea al usuario, avisa por toast y propaga el error original', () => {
    const logoutSpy = vi.spyOn(authService, 'logout').mockImplementation(() => {});
    const toastSpy  = vi.spyOn(toast, 'show');

    let errored: any;
    http.get('/api/protected').subscribe({ error: e => (errored = e) });

    httpMock.expectOne(r => r.url === '/api/protected')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    httpMock.expectOne(r => r.url.endsWith('/auth/refresh'))
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(logoutSpy).toHaveBeenCalledTimes(1);
    expect(toastSpy).toHaveBeenCalledWith('Tu sesión expiró. Ingresá nuevamente.', 'warning');
    expect(errored.status).toBe(401);
    expect(errored.url).toContain('/api/protected'); // el error propagado es el original, no el del refresh
  });

  it('un 401 en un endpoint /auth/* no dispara refresh ni logout: se propaga tal cual', () => {
    const logoutSpy = vi.spyOn(authService, 'logout').mockImplementation(() => {});

    let errored: any;
    http.post('/auth/login', { email: 'a@a.com', password: 'x' }).subscribe({ error: e => (errored = e) });

    httpMock.expectOne(r => r.url === '/auth/login')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    httpMock.expectNone(r => r.url.endsWith('/auth/refresh'));
    expect(logoutSpy).not.toHaveBeenCalled();
    expect(errored.status).toBe(401);
  });

  it('un 401 en una request ya reintentada (X-Auth-Retry) desloguea directo, sin otro refresh', () => {
    const logoutSpy = vi.spyOn(authService, 'logout').mockImplementation(() => {});
    const toastSpy  = vi.spyOn(toast, 'show');

    let errored: any;
    http.get('/api/protected', { headers: { 'X-Auth-Retry': '1' } }).subscribe({ error: e => (errored = e) });

    httpMock.expectOne(r => r.url === '/api/protected')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    httpMock.expectNone(r => r.url.endsWith('/auth/refresh'));
    expect(logoutSpy).toHaveBeenCalledTimes(1);
    expect(toastSpy).toHaveBeenCalledWith('Tu sesión expiró. Ingresá nuevamente.', 'warning');
    expect(errored.status).toBe(401);
  });
});
