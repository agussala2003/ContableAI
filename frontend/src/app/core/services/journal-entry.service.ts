import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ToastService } from './toast.service';
import { ConfigService } from '../config/config.service';

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
  lines: JournalEntryLine[];
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

  generate(transactionIds: string[]): Observable<GenerateEntriesResponse> {
    return this.http.post<GenerateEntriesResponse>(`${this.baseUrl}/generate`, {
      transactionIds,
    });
  }

  getEntries(params?: { companyId?: string; month?: number; year?: number }): Observable<JournalEntry[]> {
    let p = new HttpParams();
    if (params?.companyId) p = p.set('companyId', params.companyId);
    if (params?.month)     p = p.set('month',     params.month);
    if (params?.year)      p = p.set('year',       params.year);
    return this.http.get<JournalEntry[]>(this.baseUrl, { params: p });
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

  downloadExcel(
    companyId?: string,
    month?: number,
    year?: number,
    search?: string,
    account?: string,
    entryIds?: string[],
  ): void {
    const payload = {
      companyId,
      month,
      year,
      search,
      account,
      entryIds,
    };

    this.http.post<Blob>(`${this.baseUrl}/export`, payload, { responseType: 'blob' as 'json' }).subscribe({
      next: blob => {
        const u      = URL.createObjectURL(blob);
        const link   = document.createElement('a');
        const mLabel = month ? String(month).padStart(2, '0') : 'todo';
        const yLabel = year ?? new Date().getFullYear();
        link.href     = u;
        link.download = `LibroDiario_${mLabel}-${yLabel}.xlsx`;
        link.click();
        URL.revokeObjectURL(u);
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

  downloadHolistor(companyId?: string, month?: number, year?: number): void {
    this._downloadBlob(`${this.baseUrl}/export/holistor`, 'txt', 'Holistor', companyId, month, year);
  }

  downloadBejerman(companyId?: string, month?: number, year?: number): void {
    this._downloadBlob(`${this.baseUrl}/export/bejerman`, 'csv', 'Bejerman', companyId, month, year);
  }

  downloadCsv(companyId?: string, month?: number, year?: number): void {
    this._downloadBlob(`${this.baseUrl}/export/csv`, 'csv', 'Asientos', companyId, month, year);
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
  ): void {
    let p = new HttpParams();
    if (companyId) p = p.set('companyId', companyId);
    if (month)     p = p.set('month', month);
    if (year)      p = p.set('year',  year);
    if (search)    p = p.set('search', search);
    if (account)   p = p.set('account', account);
    if (entryIds?.length) p = p.set('entryIds', entryIds.join(','));

    this.http.get<Blob>(url, { params: p, responseType: 'blob' as 'json' }).subscribe({
      next: blob => {
        const u      = URL.createObjectURL(blob);
        const link   = document.createElement('a');
        const mLabel = month ? String(month).padStart(2, '0') : 'todo';
        const yLabel = year ?? new Date().getFullYear();
        link.href     = u;
        link.download = `${prefix}_${mLabel}-${yLabel}.${ext}`;
        link.click();
        URL.revokeObjectURL(u);
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
