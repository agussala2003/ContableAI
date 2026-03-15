import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RuleService, AccountingRule, SaveRuleRequest, RuleDirection } from '../../../../core/services/rule.service';
import { CompanyService } from '../../../../core/services/company.service';
import { ToastService } from '../../../../core/services/toast.service';
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
  private ruleService    = inject(RuleService);
  private toast          = inject(ToastService);
  companyService         = inject(CompanyService);

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
  private loadSeq = 0;

  constructor() {
    effect(() => {
      const company = this.companyService.activeCompany();
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

  onCompanySelectChange(id: string): void {
    const company = this.companyService.companies().find(c => c.id === id);
    if (company) this.companyService.selectCompany(company);
  }

  loadRules(companyId: string) {
    const requestSeq = ++this.loadSeq;
    this.isLoading.set(true);
    this.ruleService.getRules(companyId).subscribe({
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

      this.ruleService.updateRule(editing.id, req).subscribe({
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
      this.ruleService.createRule(companyId, req).subscribe({
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
    this.ruleService.deleteRule(rule.id).subscribe({
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

    this.ruleService.reapplyRule(ruleId).subscribe({
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
