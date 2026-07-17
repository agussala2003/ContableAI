import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../../core/config/config.service';
import { SkippedDuplicate, Currency } from '../../core/services/transaction';

export interface AfipVoucher {
  id: string;
  date: string;
  amount: number;
  taxName: string;
  isMatched: boolean;
  matchedTransactionId: string | null;
}

export interface AfipUploadResult {
  added: number;
  skippedDuplicates: SkippedDuplicate[];
}

export interface AfipComboVoucher {
  id: string;
  date: string;
  amount: number;
  taxName: string;
}

/** Una combinación de VEPs cuya sumatoria coincide exactamente con el movimiento. */
export interface AfipComboAlternative {
  vouchers: AfipComboVoucher[];
}

/** Un débito bancario a AFIP sin conciliar con sus combinaciones de VEPs candidatas. */
export interface AfipComboSuggestion {
  transactionId: string;
  date: string;
  description: string;
  amount: number;
  /** Siempre ARS: los combos solo se ofrecen para movimientos en pesos (guard backend). */
  currency?: Currency;
  alternatives: AfipComboAlternative[];
}

export interface ApplyCombinationResult {
  transactionId: string;
  assignedAccount: string;
  vouchersMatched: number;
}

@Injectable({
  providedIn: 'root'
})
export class AfipService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get apiUrl(): string {
    return this.configService.config().apiUrl;
  }

  // Sube PDFs, persiste vouchers y encola el job de cruce.
  uploadVouchers(companyId: string, files: File[]): Observable<AfipUploadResult> {
    const formData = new FormData();
    files.forEach(file => formData.append('files', file));
    return this.http.post<AfipUploadResult>(`${this.apiUrl}/companies/${companyId}/afip/upload`, formData);
  }

  getVouchers(companyId: string): Observable<AfipVoucher[]> {
    return this.http.get<AfipVoucher[]>(`${this.apiUrl}/companies/${companyId}/afip/vouchers`);
  }

  // Re-dispara el job de cruce manualmente (útil luego de subir extractos).
  triggerRematch(companyId: string): Observable<{ jobId: string }> {
    return this.http.post<{ jobId: string }>(`${this.apiUrl}/companies/${companyId}/afip/rematch`, {});
  }

  // Combinaciones de VEPs pendientes cuya sumatoria coincide con débitos AFIP sin conciliar.
  // Solo lectura: nada se aplica hasta que el usuario confirma.
  getCombinationSuggestions(companyId: string): Observable<AfipComboSuggestion[]> {
    return this.http.get<AfipComboSuggestion[]>(
      `${this.apiUrl}/companies/${companyId}/afip/combination-suggestions`);
  }

  // Aplica una combinación confirmada explícitamente por el usuario.
  applyCombination(companyId: string, transactionId: string, voucherIds: string[]): Observable<ApplyCombinationResult> {
    return this.http.post<ApplyCombinationResult>(
      `${this.apiUrl}/companies/${companyId}/afip/apply-combination`,
      { transactionId, voucherIds });
  }
}
