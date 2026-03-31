import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../../core/config/config.service';

export interface DashboardStats {
  totalTransactions:    number;
  pendingClassification: number;
  classified:           number;
  lowConfidence:        number;
  month:                number;
  year:                 number;
}

@Injectable({ providedIn: 'root' })
export class DashboardService {
  private http          = inject(HttpClient);
  private configService = inject(ConfigService);

  private get apiUrl(): string {
    return `${this.configService.config().apiUrl}/dashboard`;
  }

  getStats(companyId: string, month?: number, year?: number): Observable<DashboardStats> {
    let params = new HttpParams().set('companyId', companyId);
    if (month) params = params.set('month', month.toString());
    if (year)  params = params.set('year',  year.toString());
    return this.http.get<DashboardStats>(`${this.apiUrl}/stats`, { params });
  }
}
