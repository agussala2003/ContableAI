import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';

export interface AdminStats {
  totalUsers: number;
  activeUsers: number;
  pendingUsers: number;
  suspendedUsers: number;
  totalCompanies: number;
  totalTransactions: number;
  monthlyTransactions: number;
  totalJournalEntries: number;
  planDistribution: Array<{ plan: string; count: number }>;
}

export interface AdminUserRow {
  id: string;
  email: string;
  displayName: string;
  role: string;
  accountStatus: number;
  studioTenantId: string;
  createdAt: string;
  plan: string;
  companiesCount: number;
  maxCompanies: number;
  monthlyTxUsed: number;
  maxMonthlyTransactions: number;
}

export interface DbResetResponse {
  message: string;
  nextStep: string;
  seedAdminUrl: string;
  globalRulesSeeded: number;
  accountsSeeded: number;
}

@Injectable({ providedIn: 'root' })
export class AdminService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get baseUrl(): string {
    return `${this.configService.config().apiUrl}/admin`;
  }

  getStats(): Observable<AdminStats> {
    return this.http.get<AdminStats>(`${this.baseUrl}/stats`);
  }

  getUsers(): Observable<AdminUserRow[]> {
    return this.http.get<AdminUserRow[]>(`${this.baseUrl}/users`);
  }

  resetDatabase(): Observable<DbResetResponse> {
    return this.http.post<DbResetResponse>(`${this.baseUrl}/db-reset`, {});
  }
}
