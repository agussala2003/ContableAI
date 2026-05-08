import { Injectable, inject, signal, computed } from '@angular/core';
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
  studioTenantId: string | null;
  isActive: boolean;
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

export interface RuleSuggestion {
  id: string;
  keyword: string;
  suggestedAccount: string;
  frequency: number;
  createdAt: string;
}

@Injectable({ providedIn: 'root' })
export class RuleService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private _suggestionRefreshTick = signal(0);
  readonly suggestionRefreshTick = this._suggestionRefreshTick.asReadonly();

  private _transactionRefreshTick = signal(0);
  readonly transactionRefreshTick = this._transactionRefreshTick.asReadonly();

  private _globalSuggestions = signal<RuleSuggestion[]>([]);
  readonly globalSuggestions = this._globalSuggestions.asReadonly();
  readonly globalSuggestionCount = computed(() => this._globalSuggestions().length);

  triggerSuggestionRefresh(): void {
    this._suggestionRefreshTick.update(n => n + 1);
  }

  triggerTransactionRefresh(): void {
    this._transactionRefreshTick.update(n => n + 1);
  }

  loadGlobalSuggestions(companyId: string): void {
    this.getSuggestions(companyId).subscribe({
      next: res => this._globalSuggestions.set(res),
      error: () => {},
    });
  }

  clearGlobalSuggestions(): void {
    this._globalSuggestions.set([]);
  }

  removeGlobalSuggestion(id: string): void {
    this._globalSuggestions.update(s => s.filter(x => x.id !== id));
  }

  private get apiBase(): string {
    return this.configService.config().apiUrl;
  }

  getRules(companyId: string, includeInactive: boolean = false): Observable<AccountingRule[]> {
    return this.http.get<AccountingRule[]>(`${this.apiBase}/companies/${companyId}/rules?includeInactive=${includeInactive}`);
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

  activateRule(id: string): Observable<void> {
    return this.http.patch<void>(`${this.apiBase}/rules/${id}/activate`, {});
  }

  deactivateRule(id: string): Observable<void> {
    return this.http.patch<void>(`${this.apiBase}/rules/${id}/deactivate`, {});
  }

  reapplyRule(id: string): Observable<ReapplyRuleResponse> {
    return this.http.post<ReapplyRuleResponse>(`${this.apiBase}/rules/${id}/reapply`, {});
  }

  getStudioRules(includeInactive: boolean = false): Observable<AccountingRule[]> {
    return this.http.get<AccountingRule[]>(`${this.apiBase}/studio/rules?includeInactive=${includeInactive}`);
  }

  createStudioRule(req: SaveRuleRequest): Observable<AccountingRule> {
    return this.http.post<AccountingRule>(`${this.apiBase}/studio/rules`, req);
  }

  getSuggestions(companyId: string): Observable<RuleSuggestion[]> {
    return this.http.get<RuleSuggestion[]>(`${this.apiBase}/companies/${companyId}/suggestions`);
  }

  acceptSuggestion(companyId: string, suggestionId: string): Observable<{ rule: AccountingRule; updatedTransactionCount: number }> {
    return this.http.post<{ rule: AccountingRule; updatedTransactionCount: number }>(`${this.apiBase}/companies/${companyId}/suggestions/${suggestionId}/accept`, {});
  }

  rejectSuggestion(companyId: string, suggestionId: string): Observable<void> {
    return this.http.post<void>(`${this.apiBase}/companies/${companyId}/suggestions/${suggestionId}/reject`, {});
  }

  recalculateSuggestions(companyId: string): Observable<{ newSuggestions: number }> {
    return this.http.post<{ newSuggestions: number }>(`${this.apiBase}/companies/${companyId}/suggestions/recalculate`, {});
  }
}
