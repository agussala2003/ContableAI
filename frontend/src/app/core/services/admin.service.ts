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

export interface NormalizeAccountsResponse {
  transactionsScanned: number;
  transactionsUpdated: number;
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

  activateUser(id: string): Observable<{ message: string }> {
    return this.http.put<{ message: string }>(`${this.baseUrl}/users/${id}/activate`, {});
  }

  suspendUser(id: string): Observable<{ message: string }> {
    return this.http.put<{ message: string }>(`${this.baseUrl}/users/${id}/suspend`, {});
  }

  updatePlan(id: string, plan: string): Observable<{ id: string; email: string; plan: string }> {
    return this.http.patch<{ id: string; email: string; plan: string }>(`${this.baseUrl}/users/${id}/plan`, { plan });
  }

  deleteUser(id: string): Observable<{ message: string }> {
    return this.http.delete<{ message: string }>(`${this.baseUrl}/users/${id}`);
  }

  resetDatabase(): Observable<DbResetResponse> {
    return this.http.post<DbResetResponse>(`${this.baseUrl}/db-reset`, {});
  }

  normalizeAccounts(): Observable<NormalizeAccountsResponse> {
    return this.http.post<NormalizeAccountsResponse>(`${this.baseUrl}/normalize-accounts`, {});
  }
}
