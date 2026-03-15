import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';

export type RuleDirection = 'DEBIT' | 'CREDIT' | 'Debit' | 'Credit' | null;

export interface AccountingRule {
  id: string;
  keyword: string;
  targetAccount: string;
  direction: RuleDirection;
  priority: number;
  requiresTaxMatching: boolean;
  companyId: string | null;
}

export interface SaveRuleRequest {
  keyword: string;
  targetAccount: string;
  direction: 'DEBIT' | 'CREDIT' | null;
  priority: number;
  requiresTaxMatching: boolean;
}

export interface ReapplyRuleResponse {
  ruleId: string;
  updatedCount: number;
  transactionIds: string[];
  appliedAccount: string;
}

@Injectable({ providedIn: 'root' })
export class RuleService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get apiBase(): string {
    return this.configService.config().apiUrl;
  }

  getRules(companyId: string): Observable<AccountingRule[]> {
    return this.http.get<AccountingRule[]>(`${this.apiBase}/companies/${companyId}/rules`);
  }

  createRule(companyId: string, req: SaveRuleRequest): Observable<AccountingRule> {
    return this.http.post<AccountingRule>(`${this.apiBase}/companies/${companyId}/rules`, req);
  }

  updateRule(id: string, req: SaveRuleRequest): Observable<void> {
    return this.http.put<void>(`${this.apiBase}/rules/${id}`, req);
  }

  deleteRule(id: string): Observable<void> {
    return this.http.delete<void>(`${this.apiBase}/rules/${id}`);
  }

  reapplyRule(id: string): Observable<ReapplyRuleResponse> {
    return this.http.post<ReapplyRuleResponse>(`${this.apiBase}/rules/${id}/reapply`, {});
  }
}
