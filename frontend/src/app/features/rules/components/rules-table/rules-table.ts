import { Component, computed, input, output } from '@angular/core';
import { AccountingRule, RuleDirection } from '../../../../core/services/rule.service';
import { LucideAngularModule } from 'lucide-angular';
import { RuleFilterType } from '../rules.types';

@Component({
  selector: 'app-rules-table',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './rules-table.html',
  host: {
    class: 'block h-full min-h-0',
  },
})
export class RulesTable {
  rules = input<AccountingRule[]>([]);
  isLoading = input<boolean>(false);
  searchQuery = input<string>('');
  filterType = input<RuleFilterType>('all');
  deletingId = input<string | null>(null);
  overrideMapByOwnRule = input<Record<string, string[]>>({});
  overrideMapByGlobalRule = input<Record<string, string[]>>({});

  createRequested = output<void>();
  editRequested = output<AccountingRule>();
  deleteRequested = output<AccountingRule>();

  readonly displayedRules = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    const type = this.filterType();
    let list = [...this.rules()].sort((a, b) => {
      const aGlobal = a.companyId == null ? 1 : 0;
      const bGlobal = b.companyId == null ? 1 : 0;
      if (aGlobal !== bGlobal) return aGlobal - bGlobal;
      return a.priority - b.priority;
    });

    if (type === 'own') list = list.filter(r => r.companyId != null);
    if (type === 'global') list = list.filter(r => r.companyId == null);

    if (!q) return list;
    return list.filter(r =>
      r.keyword.toLowerCase().includes(q) || r.targetAccount.toLowerCase().includes(q),
    );
  });

  onCreate(): void {
    this.createRequested.emit();
  }

  onEdit(rule: AccountingRule): void {
    this.editRequested.emit(rule);
  }

  onDelete(rule: AccountingRule): void {
    this.deleteRequested.emit(rule);
  }

  typeLabel(rule: AccountingRule): 'General' | 'Propia' {
    return rule.companyId == null ? 'General' : 'Propia';
  }

  typeBadgeClass(rule: AccountingRule): string {
    return rule.companyId == null
      ? 'bg-sky-50 text-sky-700 border-sky-200 dark:bg-sky-500/10 dark:text-sky-400 dark:border-sky-500/30'
      : 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-400 dark:border-emerald-500/30';
  }

  evaluationBadge(rule: AccountingRule): string {
    return rule.companyId == null ? 'Prioridad Baja' : 'Prioridad Alta';
  }

  evaluationBadgeClass(rule: AccountingRule): string {
    return rule.companyId == null
      ? 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-700 dark:text-slate-300 dark:border-slate-600'
      : 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-400 dark:border-emerald-500/30';
  }

  directionLabel(d: RuleDirection): string {
    if (d === 'DEBIT' || d === 'Debit') return 'Debito';
    if (d === 'CREDIT' || d === 'Credit') return 'Credito';
    return 'Ambas';
  }

  directionBadgeClass(d: RuleDirection): string {
    if (d === 'DEBIT' || d === 'Debit') return 'bg-red-50 text-red-700 border-red-200 dark:bg-red-500/10 dark:text-red-400 dark:border-red-500/30';
    if (d === 'CREDIT' || d === 'Credit') return 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-400 dark:border-emerald-500/30';
    return 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-700 dark:text-slate-300 dark:border-slate-600';
  }

  overridesGlobalKeywords(rule: AccountingRule): string[] {
    return this.overrideMapByOwnRule()[rule.id] ?? [];
  }

  overriddenByOwnKeywords(rule: AccountingRule): string[] {
    return this.overrideMapByGlobalRule()[rule.id] ?? [];
  }
}
