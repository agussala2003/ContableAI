import { Component, computed, effect, inject, signal, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RuleService, AccountingRule, SaveRuleRequest, RuleDirection, PromoteRuleResponse } from '../../../../core/services/rule.service';
import { CompanyService } from '../../../../core/services/company.service';
import { ToastService } from '../../../../core/services/toast.service';
import { ChartOfAccountService } from '../../../../core/services/chart-of-account.service';
import { RuleFormSlideover, RuleFormFieldChange } from '../../components/rule-form-slideover/rule-form-slideover';
import { RulesTable } from '../../components/rules-table/rules-table';
import { RulesToolbar } from '../../components/rules-toolbar/rules-toolbar';
import { CompanyModal } from '../../../reconciliation/components/company-modal/company-modal';
import { LucideAngularModule } from 'lucide-angular';
import { Direction, RuleFilterType, RuleForm } from '../../components/rules.types';

const EMPTY_FORM = (): RuleForm => ({
  keyword: '',
  targetAccount: '',
  direction: null,
  priority: 100,
  requiresTaxMatching: false,
});

@Component({
  selector: 'app-rules-page',
  standalone: true,
  imports: [FormsModule, LucideAngularModule, CompanyModal, RulesToolbar, RulesTable, RuleFormSlideover],
  templateUrl: './rules-page.html',
})
export class RulesPage {
  private ruleService        = inject(RuleService);
  private toast              = inject(ToastService);
  companyService             = inject(CompanyService);
  chartOfAccountService      = inject(ChartOfAccountService);
  private readonly destroyRef = inject(DestroyRef);

  rules         = signal<AccountingRule[]>([]);
  isLoading     = signal(false);
  isSaving      = signal(false);
  deletingId    = signal<string | null>(null);
  showCompanyModal = signal(false);

  panelOpen     = signal(false);
  editingRule   = signal<AccountingRule | null>(null);
  form          = signal<RuleForm>(EMPTY_FORM());
  applyRetroactive = signal(true);

  searchQuery   = signal('');
  filterType    = signal<RuleFilterType>('all');
  showInactiveRules = signal(false);
  private loadSeq = 0;

  // ── Promoción a regla de estudio ────────────────────────────────────────
  /** Regla en curso de promoción; abre el modal cuando no es null. */
  promotingRule  = signal<AccountingRule | null>(null);
  /** Preview devuelto por el dry-run; null mientras se está pidiendo. */
  promotePreview = signal<PromoteRuleResponse | null>(null);
  isLoadingPreview = signal(false);
  isPromoting      = signal(false);

  constructor() {
    effect(() => {
      const company = this.companyService.activeCompany();
      const showInactive = this.showInactiveRules(); // track signal
      if (company) this.loadRules(company.id);
      else this.rules.set([]);
    });
  }

  overrideMapByOwnRule = computed(() => {
    const ownRules = this.rules().filter(r => r.companyId != null);
    const globalRules = this.rules().filter(r => r.companyId == null);
    const map: Record<string, string[]> = {};

    for (const own of ownRules) {
      const matches = globalRules
        .filter(global => this.keywordsOverlap(own.keyword, global.keyword)
          && this.directionsCompatible(own.direction, global.direction))
        .map(global => global.keyword)
        .sort((a, b) => a.localeCompare(b));

      map[own.id] = matches;
    }

    return map;
  });

  overrideMapByGlobalRule = computed(() => {
    const ownRules = this.rules().filter(r => r.companyId != null);
    const globalRules = this.rules().filter(r => r.companyId == null);
    const map: Record<string, string[]> = {};

    for (const global of globalRules) {
      const matchingOwn = ownRules
        .filter(own => this.keywordsOverlap(own.keyword, global.keyword)
          && this.directionsCompatible(own.direction, global.direction))
        .map(own => own.keyword)
        .sort((a, b) => a.localeCompare(b));

      map[global.id] = matchingOwn;
    }

    return map;
  });

  panelTitle = computed(() => this.editingRule() ? 'Editar Regla' : 'Nueva Regla');

  onSearchQueryChange(value: string): void {
    this.searchQuery.set(value);
  }

  onFilterTypeChange(value: RuleFilterType): void {
    this.filterType.set(value);
  }

  onShowInactiveChange(value: boolean): void {
    this.showInactiveRules.set(value);
  }

  onCompanySelectChange(id: string): void {
    const company = this.companyService.companies().find(c => c.id === id);
    if (company) this.companyService.selectCompany(company);
  }

  loadRules(companyId: string) {
    const requestSeq = ++this.loadSeq;
    this.isLoading.set(true);
    this.ruleService.getRules(companyId, this.showInactiveRules()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => {
        if (requestSeq !== this.loadSeq) return;
        this.rules.set(list);
        this.isLoading.set(false);
      },
      error: () => {
        if (requestSeq !== this.loadSeq) return;
        this.toast.error('Error al cargar las reglas.');
        this.isLoading.set(false);
      },
    });
  }

  onSuggestionAccepted() {
    const company = this.companyService.activeCompany();
    if (company) {
      this.loadRules(company.id);
    }
  }

  openCreate() {
    this.editingRule.set(null);
    this.form.set(EMPTY_FORM());
    this.panelOpen.set(true);
  }

  openEdit(rule: AccountingRule) {
    this.editingRule.set(rule);
    this.form.set({
      keyword:            rule.keyword,
      targetAccount:      rule.targetAccount,
      direction:          this.directionNumToStr(rule.direction),
      priority:           rule.priority,
      requiresTaxMatching: rule.requiresTaxMatching,
    });
    this.panelOpen.set(true);
  }

  closePanel() {
    this.panelOpen.set(false);
    this.editingRule.set(null);
    this.applyRetroactive.set(true);
  }

  saveRule() {
    const f = this.form();
    if (!f.keyword.trim() || !f.targetAccount.trim()) {
      this.toast.warning('Keyword y Cuenta son obligatorios.');
      return;
    }

    const req: SaveRuleRequest = {
      keyword:            f.keyword.trim(),
      targetAccount:      f.targetAccount.trim(),
      direction:          f.direction,
      priority:           f.priority,
      requiresTaxMatching: f.requiresTaxMatching,
    };

    const companyId = this.companyService.activeCompany()?.id;
    if (!companyId) { this.toast.error('No hay empresa activa.'); return; }

    this.isSaving.set(true);
    const editing = this.editingRule();

    if (editing) {
      if (editing.companyId == null) {
        this.isSaving.set(false);
        this.toast.warning('Las reglas generales son solo lectura en esta pestaña.');
        return;
      }

      this.ruleService.updateRule(editing.id, req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: () => {
          this.rules.update(list =>
            list.map(r => r.id === editing.id ? { ...r, ...req, direction: this.directionStrToNum(req.direction) } : r)
          );
          this.afterRuleSaved(editing.id, 'Regla actualizada.');
        },
        error: () => {
          this.isSaving.set(false);
          this.toast.error('Error al actualizar la regla.');
        },
      });
    } else {
      this.ruleService.createRule(companyId, req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: rule => {
          this.rules.update(list => [...list, rule].sort((a, b) => a.priority - b.priority));
          this.afterRuleSaved(rule.id, 'Regla creada.');
        },
        error: () => {
          this.isSaving.set(false);
          this.toast.error('Error al crear la regla.');
        },
      });
    }
  }

  confirmDelete(rule: AccountingRule) {
    if (rule.companyId == null) {
      this.toast.warning('Las reglas generales son solo lectura en esta pestaña.');
      return;
    }

    const ok = confirm(`¿Eliminar regla "${rule.keyword}"? Esta accion no se puede deshacer.`);
    if (!ok) return;

    this.deletingId.set(rule.id);
    this.ruleService.deleteRule(rule.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.rules.update(list => list.filter(r => r.id !== rule.id));
        this.deletingId.set(null);
        this.toast.success('Regla eliminada.');
      },
      error: () => {
        this.deletingId.set(null);
        this.toast.error('Error al eliminar la regla.');
      },
    });
  }

  toggleRuleStatus(rule: AccountingRule) {
    if (rule.companyId == null) {
      this.toast.warning('Las reglas generales son solo lectura en esta pestaña.');
      return;
    }

    const obs = rule.isActive 
      ? this.ruleService.deactivateRule(rule.id)
      : this.ruleService.activateRule(rule.id);

    obs.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.rules.update(list =>
          list.map(r => r.id === rule.id ? { ...r, isActive: !r.isActive } : r)
        );
        this.toast.success(`Regla ${rule.isActive ? 'desactivada' : 'activada'}.`);
      },
      error: () => {
        this.toast.error(`Error al ${rule.isActive ? 'desactivar' : 'activar'} la regla.`);
      }
    });
  }

  // ── Promoción a regla de estudio ────────────────────────────────────────

  /**
   * Abre el modal y pide el preview con dryRun: el backend responde a cuántas empresas del
   * estudio va a alcanzar la regla y cuáles ya tienen una propia con keyword solapado, sin
   * escribir nada. Recién al confirmar se ejecuta la promoción real.
   */
  openPromote(rule: AccountingRule): void {
    if (rule.companyId == null) {
      this.toast.warning('La regla ya aplica a todo el estudio.');
      return;
    }

    this.promotingRule.set(rule);
    this.promotePreview.set(null);
    this.isLoadingPreview.set(true);

    this.ruleService.promoteToStudio(rule.id, true).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: preview => {
        this.promotePreview.set(preview);
        this.isLoadingPreview.set(false);
      },
      error: err => {
        this.isLoadingPreview.set(false);
        this.closePromote();
        this.toast.error(this.promoteErrorMessage(err, 'No se pudo calcular el impacto de la promoción.'));
      },
    });
  }

  closePromote(): void {
    this.promotingRule.set(null);
    this.promotePreview.set(null);
    this.isLoadingPreview.set(false);
    this.isPromoting.set(false);
  }

  confirmPromote(): void {
    const rule = this.promotingRule();
    if (!rule) return;

    this.isPromoting.set(true);
    this.ruleService.promoteToStudio(rule.id, false).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: res => {
        // La regla conserva su id: alcanza con vaciar companyId en la copia local para que
        // rules-table la reclasifique como "Estudio" y la mueva de la pestaña Propias a Estudio.
        // studioTenantId ya venía cargado y sigue siendo el correcto.
        this.rules.update(list =>
          list.map(r => r.id === rule.id ? { ...r, companyId: null } : r)
        );

        this.closePromote();
        this.toast.success(
          `"${rule.keyword}" ahora aplica a ${res.affectedCompanies} empresa${res.affectedCompanies !== 1 ? 's' : ''} del estudio.`
        );
      },
      error: err => {
        this.isPromoting.set(false);
        this.toast.error(this.promoteErrorMessage(err, 'No se pudo promover la regla.'));
      },
    });
  }

  /** El backend devuelve ProblemDetails en los 422 (regla ya de estudio, regla sin estudio). */
  private promoteErrorMessage(err: unknown, fallback: string): string {
    const problem = (err as { error?: { detail?: string; title?: string } } | null)?.error;
    return problem?.detail ?? problem?.title ?? fallback;
  }

  updateFormField<K extends keyof RuleForm>(field: K, value: RuleForm[K]): void {
    this.form.update(f => ({ ...f, [field]: value }));
  }

  onFormFieldChange(change: RuleFormFieldChange): void {
    this.updateFormField(change.field, change.value as RuleForm[typeof change.field]);
  }

  onApplyRetroactiveChange(value: boolean): void {
    this.applyRetroactive.set(value);
  }

  directionNumToStr(d: RuleDirection): Direction {
    if (d === 'DEBIT' || d === 'Debit') return 'DEBIT';
    if (d === 'CREDIT' || d === 'Credit') return 'CREDIT';
    return null;
  }

  directionStrToNum(d: Direction): RuleDirection {
    if (d === 'DEBIT')  return 'DEBIT';
    if (d === 'CREDIT') return 'CREDIT';
    return null;
  }

  directionLabel(d: RuleDirection): string {
    if (d === 'DEBIT' || d === 'Debit') return 'Débito';
    if (d === 'CREDIT' || d === 'Credit') return 'Crédito';
    return 'Ambas';
  }

  directionBadgeClass(d: RuleDirection): string {
    if (d === 'DEBIT' || d === 'Debit') return 'bg-red-50 text-red-700 border-red-200 dark:bg-red-500/10 dark:text-red-400 dark:border-red-500/30';
    if (d === 'CREDIT' || d === 'Credit') return 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-400 dark:border-emerald-500/30';
    return 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-700 dark:text-slate-300 dark:border-slate-600';
  }

  private normalizeKeyword(value: string): string {
    return value.trim().toLowerCase().replace(/\s+/g, ' ');
  }

  private keywordsOverlap(a: string, b: string): boolean {
    const left = this.normalizeKeyword(a);
    const right = this.normalizeKeyword(b);
    if (!left || !right) return false;
    return left.includes(right) || right.includes(left);
  }

  private directionsCompatible(a: RuleDirection, b: RuleDirection): boolean {
    const left = this.directionNumToStr(a);
    const right = this.directionNumToStr(b);
    if (left == null || right == null) return true;
    return left === right;
  }

  private afterRuleSaved(ruleId: string, successMessage: string): void {
    if (!this.applyRetroactive()) {
      this.isSaving.set(false);
      this.closePanel();
      this.toast.success(successMessage);
      return;
    }

    this.ruleService.reapplyRule(ruleId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => {
        this.isSaving.set(false);
        this.closePanel();
        this.toast.success(`${successMessage} Reaplicada en ${result.updatedCount} movimiento(s) pendiente(s).`);
      },
      error: () => {
        this.isSaving.set(false);
        this.closePanel();
        this.toast.warning(`${successMessage} No se pudo completar la reaplicación automática.`);
      },
    });
  }
}
