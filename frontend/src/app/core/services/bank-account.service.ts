import { Injectable, inject } from '@angular/core';
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
