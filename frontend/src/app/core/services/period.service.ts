import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';

export interface ClosedPeriod {
  id: number;
  studioTenantId: string;
  year: number;
  month: number;
  closedAt: string;
  closedByEmail: string;
}

@Injectable({ providedIn: 'root' })
export class PeriodService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get base(): string {
    return `${this.configService.config().apiUrl}/periods`;
  }

  getClosedPeriods(): Observable<ClosedPeriod[]> {
    return this.http.get<ClosedPeriod[]>(`${this.base}/closed`);
  }

  closePeriod(year: number, month: number): Observable<ClosedPeriod> {
    return this.http.post<ClosedPeriod>(`${this.base}/close`, { year, month });
  }

  reopenPeriod(year: number, month: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/${year}/${month}`);
  }

  isPeriodClosed(closedPeriods: ClosedPeriod[], year: number, month: number): boolean {
    return closedPeriods.some(p => p.year === year && p.month === month);
  }

  MONTHS = ['Enero','Febrero','Marzo','Abril','Mayo','Junio',
             'Julio','Agosto','Septiembre','Octubre','Noviembre','Diciembre'];
}
