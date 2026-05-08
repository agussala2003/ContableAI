import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../../core/config/config.service';
import { SkippedDuplicate } from '../../core/services/transaction';

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
}
