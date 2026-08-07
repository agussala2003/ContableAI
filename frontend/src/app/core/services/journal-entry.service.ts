import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ToastService } from './toast.service';
import { ConfigService } from '../config/config.service';
import { SKIP_LOADING } from '../interceptors/loading.interceptor';
import { saveBlob, saveResponseAsFile } from '../utils/file-download';
import { BankAccountOption } from './transaction';

const MONTH_NAMES = [
  'Enero', 'Febrero', 'Marzo', 'Abril', 'Mayo', 'Junio',
  'Julio', 'Agosto', 'Septiembre', 'Octubre', 'Noviembre', 'Diciembre',
];

export interface JournalEntryLine {
  account: string;
  amount: number;
  isDebit: boolean;
}

export interface JournalEntry {
  id: string;
  date: string;
  description: string;
  companyId: string | null;
  bankTransactionId: string;
  generatedAt: string;
  /** Moneda del asiento (ISO 4217: "ARS" | "USD"). */
  currency: string;
  /** Cuenta bancaria que originó el asiento; null en asientos previos a la multi-cuenta. */
  bankAccountId: string | null;
  lines: JournalEntryLine[];
}

/** Respuesta del listado: los asientos más las opciones del filtro por cuenta bancaria. */
export interface JournalEntriesResponse {
  entries: JournalEntry[];
  availableBankAccounts: BankAccountOption[];
}

export interface LinkedTransaction {
  transactionId: string;
  journalEntryId: string;
}

export interface GenerateEntriesResponse {
  generated: number;
  duplicatesSkipped?: number;
  bankAccount?: string;
  entries?: JournalEntry[];
  linkedTransactions?: LinkedTransaction[];
  message?: string;
}

@Injectable({ providedIn: 'root' })
export class JournalEntryService {
  private http  = inject(HttpClient);
  private toast = inject(ToastService);
  private configService = inject(ConfigService);

  private get baseUrl(): string {
    return `${this.configService.config().apiUrl}/journal-entries`;
  }

  generate(transactionIds: string[]): Observable<{ jobId?: string; message: string }> {
    return this.http.post<{ jobId?: string; message: string }>(`${this.baseUrl}/generate`, {
      transactionIds,
    });
  }

  // Polling cada 3s: SKIP_LOADING evita que cada ciclo dispare el overlay global bloqueante.
  getJobStatus(jobId: string): Observable<{ jobId: string; state: string; createdAt: string }> {
    return this.http.get<{ jobId: string; state: string; createdAt: string }>(
      `${this.configService.config().apiUrl}/jobs/${jobId}/status`,
      { context: new HttpContext().set(SKIP_LOADING, true) },
    );
  }

  getEntries(params?: {
    companyId?: string; month?: number; year?: number; currency?: string; bankAccountId?: string;
  }): Observable<JournalEntriesResponse> {
    let p = new HttpParams();
    if (params?.companyId) p = p.set('companyId', params.companyId);
    if (params?.month)     p = p.set('month',     params.month);
    if (params?.year)      p = p.set('year',       params.year);
    if (params?.currency)  p = p.set('currency',   params.currency);
    if (params?.bankAccountId) p = p.set('bankAccountId', params.bankAccountId);
    return this.http.get<JournalEntriesResponse>(this.baseUrl, { params: p });
  }

  deleteEntry(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

  deleteAll(params?: { companyId?: string; month?: number; year?: number }): Observable<{
    deletedEntries: number;
    unlinkedTransactions: number;
    scopeDescription: string;
  }> {
    let p = new HttpParams();
    if (params?.companyId) p = p.set('companyId', params.companyId);
    if (params?.month)     p = p.set('month',     params.month);
    if (params?.year)      p = p.set('year',      params.year);

    return this.http.delete<{
      deletedEntries: number;
      unlinkedTransactions: number;
      scopeDescription: string;
    }>(this.baseUrl, { params: p });
  }

  /**
   * Nombre por defecto del Libro Diario: `Libro_Diario_{Empresa}_{Mes}_{Año}.xlsx`. Se arma acá y
   * no en el componente para que el prompt y el nombre real del archivo no puedan divergir.
   */
  buildExcelFileName(companyName?: string, month?: number, year?: number): string {
    const company = (companyName ?? 'Empresa').replace(/[^\p{L}\p{N}]+/gu, '_').replace(/^_|_$/g, '');
    const mLabel  = month ? MONTH_NAMES[month - 1] : 'Todos';
    const yLabel  = year ?? new Date().getFullYear();
    return `Libro_Diario_${company}_${mLabel}_${yLabel}.xlsx`;
  }

  downloadExcel(
    companyId?: string,
    month?: number,
    year?: number,
    search?: string,
    account?: string,
    entryIds?: string[],
    currency?: string,
    fileName?: string,
  ): void {
    const payload = {
      companyId,
      month,
      year,
      search,
      account,
      currency,
      entryIds,
    };

    this.http.post(`${this.baseUrl}/export`, payload, {
      observe: 'response',
      responseType: 'blob',
    }).subscribe({
      next: response => {
        // El nombre elegido por el usuario gana sobre el Content-Disposition del backend, así que
        // se pasa como fallback Y se fuerza: saveResponseAsFile prefiere el header cuando existe.
        const fallback = fileName ?? this.buildExcelFileName(undefined, month, year);
        if (fileName) saveBlob(response.body!, fileName);
        else          saveResponseAsFile(response, fallback);
      },
      error: err => {
        const status = err?.status;
        if (status === 404) {
          this.toast.error('No hay asientos para exportar en el período seleccionado.');
        } else {
          this.toast.error('Error al generar el archivo de exportación.');
        }
      },
    });
  }

  downloadHolistor(companyId?: string, month?: number, year?: number, currency?: string): void {
    this._downloadBlob(`${this.baseUrl}/export/holistor`, 'txt', 'Holistor', companyId, month, year, undefined, undefined, undefined, currency);
  }

  downloadBejerman(companyId?: string, month?: number, year?: number, currency?: string): void {
    this._downloadBlob(`${this.baseUrl}/export/bejerman`, 'csv', 'Bejerman', companyId, month, year, undefined, undefined, undefined, currency);
  }

  downloadCsv(companyId?: string, month?: number, year?: number, currency?: string): void {
    this._downloadBlob(`${this.baseUrl}/export/csv`, 'csv', 'Asientos', companyId, month, year, undefined, undefined, undefined, currency);
  }

  private _downloadBlob(
    url: string,
    ext: string,
    prefix: string,
    companyId?: string,
    month?: number,
    year?: number,
    search?: string,
    account?: string,
    entryIds?: string[],
    currency?: string,
  ): void {
    let p = new HttpParams();
    if (companyId) p = p.set('companyId', companyId);
    if (month)     p = p.set('month', month);
    if (year)      p = p.set('year',  year);
    if (search)    p = p.set('search', search);
    if (account)   p = p.set('account', account);
    if (currency)  p = p.set('currency', currency);
    if (entryIds?.length) p = p.set('entryIds', entryIds.join(','));

    this.http.get(url, { params: p, observe: 'response', responseType: 'blob' }).subscribe({
      next: response => {
        const mLabel = month ? String(month).padStart(2, '0') : 'todo';
        const yLabel = year ?? new Date().getFullYear();
        saveResponseAsFile(response, `${prefix}_${mLabel}-${yLabel}.${ext}`);
      },
      error: err => {
        const status = err?.status;
        if (status === 404) {
          this.toast.error('No hay asientos para exportar en el período seleccionado.');
        } else {
          this.toast.error('Error al generar el archivo de exportación.');
        }
      },
    });
  }
}
