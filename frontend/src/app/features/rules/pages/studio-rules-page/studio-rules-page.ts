import { Component, computed, signal, inject, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RuleService, AccountingRule, SaveRuleRequest } from '../../../../core/services/rule.service';
import { ToastService } from '../../../../core/services/toast.service';
import { ChartOfAccountService } from '../../../../core/services/chart-of-account.service';
import { RuleFormSlideover, RuleFormFieldChange } from '../../components/rule-form-slideover/rule-form-slideover';
import { RulesTable } from '../../components/rules-table/rules-table';
import { LucideAngularModule } from 'lucide-angular';
import { Direction, RuleForm } from '../../components/rules.types';

const EMPTY_FORM = (): RuleForm => ({
  keyword: '',
  targetAccount: '',
  direction: null,
  priority: 100,
  requiresTaxMatching: false,
});

@Component({
  selector: 'app-studio-rules-page',
  standalone: true,
  imports: [LucideAngularModule, RulesTable, RuleFormSlideover],
  templateUrl: './studio-rules-page.html',
})
export class StudioRulesPage {
  private ruleService       = inject(RuleService);
  private toast             = inject(ToastService);
  chartOfAccountService     = inject(ChartOfAccountService);
  private readonly destroyRef = inject(DestroyRef);

  rules      = signal<AccountingRule[]>([]);
  isLoading  = signal(false);
  isSaving   = signal(false);
  deletingId = signal<string | null>(null);

  panelOpen   = signal(false);
  editingRule = signal<AccountingRule | null>(null);
  form        = signal<RuleForm>(EMPTY_FORM());
  searchQuery = signal('');
  showInactive = signal(false);

  panelTitle = computed(() => this.editingRule() ? 'Editar Regla de Estudio' : 'Nueva Regla de Estudio');

  constructor() {
    this.loadRules();
  }

  loadRules() {
    this.isLoading.set(true);
    this.ruleService.getStudioRules(this.showInactive()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: list => { this.rules.set(list); this.isLoading.set(false); },
      error: () => { this.toast.error('Error al cargar las reglas del estudio.'); this.isLoading.set(false); },
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
      keyword:             rule.keyword,
      targetAccount:       rule.targetAccount,
      direction:           this.directionToStr(rule.direction),
      priority:            rule.priority,
      requiresTaxMatching: rule.requiresTaxMatching,
    });
    this.panelOpen.set(true);
  }

  closePanel() {
    this.panelOpen.set(false);
    this.editingRule.set(null);
  }

  saveRule() {
    const f = this.form();
    if (!f.keyword.trim() || !f.targetAccount.trim()) {
      this.toast.warning('Keyword y Cuenta son obligatorios.');
      return;
    }

    const req: SaveRuleRequest = {
      keyword:             f.keyword.trim(),
      targetAccount:       f.targetAccount.trim(),
      direction:           f.direction,
      priority:            f.priority,
      requiresTaxMatching: f.requiresTaxMatching,
    };

    this.isSaving.set(true);
    const editing = this.editingRule();

    if (editing) {
      this.ruleService.updateRule(editing.id, req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: () => {
          this.rules.update(list =>
            list.map(r => r.id === editing.id ? { ...r, ...req, direction: req.direction } : r)
          );
          this.isSaving.set(false);
          this.closePanel();
          this.toast.success('Regla actualizada.');
        },
        error: () => { this.isSaving.set(false); this.toast.error('Error al actualizar la regla.'); },
      });
    } else {
      this.ruleService.createStudioRule(req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: rule => {
          this.rules.update(list => [...list, rule].sort((a, b) => a.priority - b.priority));
          this.isSaving.set(false);
          this.closePanel();
          this.toast.success('Regla de estudio creada.');
        },
        error: () => { this.isSaving.set(false); this.toast.error('Error al crear la regla.'); },
      });
    }
  }

  confirmDelete(rule: AccountingRule) {
    if (!confirm(`¿Eliminar regla "${rule.keyword}"?`)) return;
    this.deletingId.set(rule.id);
    this.ruleService.deleteRule(rule.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.rules.update(list => list.filter(r => r.id !== rule.id));
        this.deletingId.set(null);
        this.toast.success('Regla eliminada.');
      },
      error: () => { this.deletingId.set(null); this.toast.error('Error al eliminar la regla.'); },
    });
  }

  toggleRuleStatus(rule: AccountingRule) {
    const obs = rule.isActive ? this.ruleService.deactivateRule(rule.id) : this.ruleService.activateRule(rule.id);
    obs.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.rules.update(list => list.map(r => r.id === rule.id ? { ...r, isActive: !r.isActive } : r));
        this.toast.success(`Regla ${rule.isActive ? 'desactivada' : 'activada'}.`);
      },
      error: () => this.toast.error('Error al cambiar el estado de la regla.'),
    });
  }

  onFormFieldChange(change: RuleFormFieldChange): void {
    this.form.update(f => ({ ...f, [change.field]: change.value }));
  }

  noop(): void {}

  private directionToStr(d: string | null): Direction {
    if (d === 'DEBIT' || d === 'Debit') return 'DEBIT';
    if (d === 'CREDIT' || d === 'Credit') return 'CREDIT';
    return null;
  }
}
