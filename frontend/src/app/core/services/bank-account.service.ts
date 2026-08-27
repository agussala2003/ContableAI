import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';
import { Currency } from './transaction';

export interface BankAccount {
  id: string;
  companyId: string;
  /** Nombre con el que el contador identifica la cuenta. */
  alias: string;
  /** Número tal como figura en el extracto, con su formato original. */
  accountNumber: string | null;
  /** Solo dígitos; lo usa el enrutamiento automático del OCR. Lo deriva el backend. */
  normalizedNumber: string | null;
  cbu: string | null;
  bankCode: string | null;
  currency: Currency;
  /** Contrapartida contable de los asientos. Vacía = cuenta provisional, no puede asentar. */
  contraAccountName: string;
  chartOfAccountId: string | null;
  isActive: boolean;
}

/** Banco asignable a una cuenta. Lo sirve el backend desde `Domain.Constants.BankCodes`. */
export interface BankCodeOption {
  code: string;
  displayName: string;
}

export interface SaveBankAccountRequest {
  alias: string;
  accountNumber?: string | null;
  cbu?: string | null;
  bankCode?: string | null;
  currency: Currency;
  contraAccountName?: string | null;
}

@Injectable({ providedIn: 'root' })
export class BankAccountService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get apiBase(): string {
    return this.configService.config().apiUrl;
  }

  // ── Estado compartido ──────────────────────────────────────────────────
  // Lo consume el selector de cuenta de la Dropzone. Vive en el servicio y no en el componente
  // porque el alta de una cuenta ocurre en otra pantalla (la ficha de empresa): sin un estado
  // común, el selector seguiría ofreciendo la lista vieja hasta recargar la página.

  private _accounts = signal<BankAccount[]>([]);
  private loadedFor: string | null = null;

  /** Cuentas activas de la empresa en foco: las únicas a las que tiene sentido dirigir una carga. */
  readonly activeAccounts = computed(() => this._accounts().filter(a => a.isActive));

  // ── Catálogo de bancos ─────────────────────────────────────────────────
  // Se sirve desde el backend (`/bank-codes`) en lugar de estar escrito en el frontend. El
  // formulario de cuentas tenía la lista hardcodeada y se le había quedado Santander afuera: no
  // había forma de asignárselo a una cuenta, y sin banco la cuenta queda fuera de su propio filtro.

  private _bankCodes = signal<BankCodeOption[]>([]);
  private bankCodesLoaded = false;

  readonly bankCodes = this._bankCodes.asReadonly();

  private bankCodeLabels = computed(() =>
    new Map(this._bankCodes().map(b => [b.code, b.displayName]))
  );

  /** Carga el catálogo una sola vez por sesión: es una constante del sistema, no cambia. */
  loadBankCodes(): void {
    if (this.bankCodesLoaded) return;
    this.bankCodesLoaded = true;

    this.http.get<BankCodeOption[]>(`${this.apiBase}/bank-codes`).subscribe({
      next: list => this._bankCodes.set(list),
      // Sin catálogo el selector queda vacío, pero el resto de la pantalla sigue funcionando:
      // el banco es opcional en una cuenta.
      error: () => { this.bankCodesLoaded = false; },
    });
  }

  /** Nombre del banco para mostrar. Cae al código crudo antes que a una cadena vacía. */
  bankLabel(code: string | null | undefined): string {
    if (!code) return '';
    return this.bankCodeLabels().get(code) ?? code;
  }

  /** (Re)carga el estado compartido. Llamar al cambiar de empresa y tras cada alta/baja. */
  refresh(companyId: string | null | undefined): void {
    if (!companyId) {
      this.loadedFor = null;
      this._accounts.set([]);
      return;
    }

    this.loadedFor = companyId;
    this.list(companyId, true).subscribe({
      // Descarta respuestas de una empresa que ya no es la activa (el usuario pudo cambiarla
      // mientras la request estaba en vuelo).
      next: list => { if (this.loadedFor === companyId) this._accounts.set(list); },
      error: ()   => { if (this.loadedFor === companyId) this._accounts.set([]); },
    });
  }

  list(companyId: string, includeInactive = false): Observable<BankAccount[]> {
    const params = new HttpParams().set('includeInactive', includeInactive);
    return this.http.get<BankAccount[]>(`${this.apiBase}/companies/${companyId}/bank-accounts`, { params });
  }

  create(companyId: string, req: SaveBankAccountRequest): Observable<BankAccount> {
    return this.http.post<BankAccount>(`${this.apiBase}/companies/${companyId}/bank-accounts`, req);
  }

  update(id: string, req: SaveBankAccountRequest): Observable<BankAccount> {
    return this.http.put<BankAccount>(`${this.apiBase}/bank-accounts/${id}`, req);
  }

  deactivate(id: string): Observable<BankAccount> {
    return this.http.patch<BankAccount>(`${this.apiBase}/bank-accounts/${id}/deactivate`, {});
  }

  activate(id: string): Observable<BankAccount> {
    return this.http.patch<BankAccount>(`${this.apiBase}/bank-accounts/${id}/activate`, {});
  }
}
