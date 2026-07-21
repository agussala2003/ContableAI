import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';
import { SKIP_LOADING } from '../interceptors/loading.interceptor';

export type Currency = 'ARS' | 'USD';

export interface BankTransaction {
  id: string;
  date: string;
  description: string;
  externalId: string | null;
  amount: number;
  currency: Currency;
  type: number; // 0 = Debit, 1 = Credit
  assignedAccount: string;
  needsTaxMatching: boolean;
  classificationSource: string;
  confidenceScore: number;   // 0.0 (rojo) → 0.65 (amarillo) → 1.0 (verde)
  needsBreakdown: boolean;   // F3-3: pago de tarjeta que requiere desglose
  isPossibleDuplicate: boolean; // F3-2: posible duplicado sindical
  tenantId: string;
  companyId: string | null;
  journalEntryId: string | null;
}

export interface CurrencyTotals {
  currency: Currency;
  ingresos: number;
  egresos: number;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
  totalIngresosFiltered: number;
  totalEgresosFiltered: number;
  totalIngresosAll: number;
  totalEgresosAll: number;
  currencyTotals?: CurrencyTotals[];
  availableAccounts?: string[];
  availableMonths?: number[];
  availableYears?: number[];
}

export interface TransactionQueryParams {
  companyId?:    string;
  month?:        number;
  year?:         number;
  search?:       string;
  account?:      string;
  direction?:    'debit' | 'credit';
  sortBy?:       string;
  sortDir?:      'asc' | 'desc';
  strictSearch?: boolean;
  minAmount?:    number;
  maxAmount?:    number;
  exactAmount?:  number;
  currency?:     Currency;
  page?:         number;
  pageSize?:     number;
}

export interface SkippedDuplicate {
  date: string;
  amount: number;
  description: string;
  currency?: Currency;
}

export interface PerFileResult {
  fileName: string;
  processed: number;
  duplicatesSkipped: number;
  transactions: BankTransaction[];
}

export interface UploadResponse {
  totalFiles: number;
  totalProcessed: number;
  duplicatesSkipped: number;
  reappliedToExisting?: number;
  companyName: string;
  perFile: PerFileResult[];
  skippedDuplicates?: SkippedDuplicate[];
  parseErrors?: string[];
}

/** Respuesta del endpoint de subida: el archivo se procesa en un job de Hangfire en segundo plano. */
export interface EnqueueUploadResponse {
  uploadId: string;
  message: string;
}

/**
 * Resultado polleado de un job de subida (GET /transactions/upload/{uploadId}/result).
 * `done` es false mientras el job no terminó — nunca 404, para no disparar el interceptor
 * global de errores en cada ciclo de polling.
 */
export interface UploadJobResultEnvelope {
  done: boolean;
  isSuccess?: boolean;
  statusCode?: number;
  error?: string | null;
  value?: UploadResponse | null;
}

export interface UpdateTransactionResponse {
  transaction: BankTransaction;
  newSuggestionKeyword: string | null;
}

export interface BulkUpdateResponse {
  updatedCount: number;
  assignedAccount: string;
  appliedRuleId?: string | null;
  classificationSource?: string;
  transactions: BankTransaction[];
  newSuggestionKeywords?: string[];
}

export interface AfipMatchResponse {
  totalPresentationsRead: number;
  successfulMatches: number;
  stillPending: number;
}

@Injectable({ providedIn: 'root' })
export class Transaction {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get apiUrl(): string {
    return `${this.configService.config().apiUrl}/transactions`;
  }

  private get baseUrl(): string {
    return this.configService.config().apiUrl;
  }

  /** Encola el procesamiento del extracto (parseo/OCR/clasificación) como job de Hangfire; el
   * resultado se consulta con `getUploadResult` (polling). */
  uploadFiles(files: File[], bankCode: string, companyId?: string, withoutDateFilter = false, forceReapplyRules = false): Observable<EnqueueUploadResponse> {
    const formData = new FormData();
    for (const file of files) formData.append('files', file, file.name);
    if (bankCode) formData.append('bankCode', bankCode);
    if (companyId) formData.append('companyId', companyId);
    formData.append('withoutDateFilter', withoutDateFilter ? 'true' : 'false');
    formData.append('forceReapplyRules', forceReapplyRules ? 'true' : 'false');
    return this.http.post<EnqueueUploadResponse>(`${this.apiUrl}/upload`, formData);
  }

  // Polling cada 2s: SKIP_LOADING evita que cada ciclo dispare el overlay global bloqueante.
  getUploadResult(uploadId: string): Observable<UploadJobResultEnvelope> {
    return this.http.get<UploadJobResultEnvelope>(`${this.apiUrl}/upload/${uploadId}/result`, {
      context: new HttpContext().set(SKIP_LOADING, true),
    });
  }

  getTransactions(params: TransactionQueryParams = {}): Observable<PagedResult<BankTransaction>> {
    let httpParams = new HttpParams();
    if (params.companyId)   httpParams = httpParams.set('companyId', params.companyId);
    if (params.month)        httpParams = httpParams.set('month', params.month.toString());
    if (params.year)         httpParams = httpParams.set('year', params.year.toString());
    if (params.search)       httpParams = httpParams.set('search', params.search);
    if (params.account)      httpParams = httpParams.set('account', params.account);
    if (params.sortBy)                       httpParams = httpParams.set('sortBy', params.sortBy);
    if (params.sortDir)                      httpParams = httpParams.set('sortDir', params.sortDir);
    if (params.strictSearch)                 httpParams = httpParams.set('strictSearch', 'true');
    if (params.direction === 'debit')        httpParams = httpParams.set('type', '0');
    if (params.direction === 'credit')       httpParams = httpParams.set('type', '1');
    if (params.minAmount !== undefined)      httpParams = httpParams.set('minAmount', params.minAmount.toString());
    if (params.maxAmount !== undefined)      httpParams = httpParams.set('maxAmount', params.maxAmount.toString());
    if (params.exactAmount !== undefined)    httpParams = httpParams.set('exactAmount', params.exactAmount.toString());
    if (params.currency)                     httpParams = httpParams.set('currency', params.currency);
    if (params.page)                         httpParams = httpParams.set('page', params.page.toString());
    if (params.pageSize)                     httpParams = httpParams.set('pageSize', params.pageSize.toString());
    return this.http.get<PagedResult<BankTransaction>>(this.apiUrl, { params: httpParams });
  }

  updateTransactionAccount(id: string, newAccount: string): Observable<UpdateTransactionResponse> {
    return this.http.put<UpdateTransactionResponse>(`${this.apiUrl}/${id}`, { assignedAccount: newAccount });
  }

  bulkUpdate(ids: string[], assignedAccount: string, ruleId?: string): Observable<BulkUpdateResponse> {
    return this.http.put<BulkUpdateResponse>(`${this.apiUrl}/bulk`, { ids, assignedAccount, ruleId: ruleId ?? null });
  }

  /** Devuelve todos los IDs de transacciones con cuenta asignada y sin asiento (sin páginación). */
  getUnbookedIds(companyId?: string): Observable<string[]> {
    let params = new HttpParams();
    if (companyId) params = params.set('companyId', companyId);
    return this.http.get<string[]>(`${this.apiUrl}/unbooked-ids`, { params });
  }

  deleteAllTransactions(): Observable<{ message: string }> {
    return this.http.delete<{ message: string }>(this.apiUrl);
  }

  matchAfip(files: File[], companyId?: string): Observable<AfipMatchResponse> {
    const formData = new FormData();
    for (const f of files) formData.append('files', f, f.name);
    if (companyId) formData.append('companyId', companyId);
    return this.http.post<AfipMatchResponse>(`${this.baseUrl}/afip/match`, formData);
  }

  downloadCsv(companyId?: string, month?: number, year?: number): Observable<void> {
    let params = new HttpParams();
    if (companyId) params = params.set('companyId', companyId);
    if (month)     params = params.set('month', month);
    if (year)      params = params.set('year',  year);

    return new Observable<void>(observer => {
      this.http.get<Blob>(`${this.apiUrl}/export`, { params, responseType: 'blob' as 'json' }).subscribe({
        next: blob => {
          const url  = URL.createObjectURL(blob);
          const link = document.createElement('a');
          const m    = month ? String(month).padStart(2, '0') : 'todo';
          const y    = year ?? new Date().getFullYear();
          link.href  = url;
          link.setAttribute('download', `Banco_${m}-${y}.csv`);
          document.body.appendChild(link);
          link.click();
          document.body.removeChild(link);
          URL.revokeObjectURL(url);
          observer.next();
          observer.complete();
        },
        error: err => observer.error(err),
      });
    });
  }
}