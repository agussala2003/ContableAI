import { Injectable, inject, signal, computed } from '@angular/core';
import { HttpClient, HttpContext } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';
import { SKIP_LOADING } from '../interceptors/loading.interceptor';

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

/** Impacto de la reaplicación forzada, con el desglose por origen previo de la clasificación. */
export interface ReapplyRuleReport {
  ruleId: string;
  keyword: string;
  targetAccount: string;
  dryRun: boolean;
  /** Movimientos que la operación va a modificar. */
  totalToUpdate: number;
  /** De esos: estaban sin categorizar. */
  pending: number;
  /** De esos: los había clasificado otra regla. */
  byOtherRule: number;
  /** De esos: los asignó el contador a mano. Es el que dispara la advertencia destructiva. */
  manual: number;
  skippedSettled: number;
  skippedClosedPeriod: number;
  skippedAfipCombo: number;
  alreadyApplied: number;
}

export interface JobStatus {
  jobId: string;
  /** Estado de Hangfire: "Enqueued" | "Processing" | "Succeeded" | "Failed" | "Deleted". */
  state: string;
  createdAt: string;
}

/** Regla propia de otra empresa que le va a seguir ganando a la regla promovida. */
export interface PromoteRuleConflict {
  companyId: string;
  companyName: string;
  keyword: string;
  direction: RuleDirection;
}

export interface PromoteRuleResponse {
  ruleId: string;
  keyword: string;
  targetAccount: string;
  dryRun: boolean;
  /** Empresas activas del estudio a las que pasa a aplicar la regla. */
  affectedCompanies: number;
  /** Cuántas de esas empresas ya tienen una regla propia con keyword solapado. */
  conflictingCompanies: number;
  conflicts: PromoteRuleConflict[];
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

  /** Preview de la reaplicación forzada: calcula el impacto sin escribir nada. */
  reapplyPreview(id: string): Observable<ReapplyRuleReport> {
    return this.http.post<ReapplyRuleReport>(
      `${this.apiBase}/rules/${id}/reapply-async?dryRun=true`, {},
    );
  }

  /** Encola la reaplicación forzada en Hangfire; el progreso se sigue con {@link getJobStatus}. */
  reapplyAsync(id: string): Observable<{ jobId: string; message: string }> {
    return this.http.post<{ jobId: string; message: string }>(
      `${this.apiBase}/rules/${id}/reapply-async`, {},
    );
  }

  /**
   * Estado de un job de Hangfire. Endpoint genérico (no específico de reglas); se expone acá
   * para que la página de reglas no tenga que depender de otra feature solo por el polling.
   * SKIP_LOADING evita que cada ciclo dispare el overlay global bloqueante.
   */
  getJobStatus(jobId: string): Observable<JobStatus> {
    return this.http.get<JobStatus>(`${this.apiBase}/jobs/${jobId}/status`, {
      context: new HttpContext().set(SKIP_LOADING, true),
    });
  }

  /**
   * Cambia el alcance de una regla de empresa a nivel estudio, conservando su id.
   * Con `dryRun` en true devuelve el preview (empresas alcanzadas y conflictos) sin escribir.
   */
  promoteToStudio(id: string, dryRun: boolean): Observable<PromoteRuleResponse> {
    return this.http.post<PromoteRuleResponse>(
      `${this.apiBase}/rules/${id}/promote-to-studio?dryRun=${dryRun}`, {},
    );
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
