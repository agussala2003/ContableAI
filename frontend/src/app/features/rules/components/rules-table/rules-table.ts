import { Component, HostListener, computed, input, output, signal } from '@angular/core';
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

  promotingId = input<string | null>(null);
  reapplyingId = input<string | null>(null);

  createRequested = output<void>();
  /** Reglas tildadas para exportar. */
  exportRequested = output<AccountingRule[]>();
  editRequested = output<AccountingRule>();
  deleteRequested = output<AccountingRule>();
  toggleStatusRequested = output<AccountingRule>();
  promoteRequested = output<AccountingRule>();
  reapplyRequested = output<AccountingRule>();

  readonly displayedRules = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    const type = this.filterType();
    const scopeOrder = (r: AccountingRule) => r.companyId != null ? 0 : r.studioTenantId != null ? 1 : 2;
    let list = [...this.rules()].sort((a, b) => {
      const diff = scopeOrder(a) - scopeOrder(b);
      return diff !== 0 ? diff : a.priority - b.priority;
    });

    if (type === 'own') list = list.filter(r => r.companyId != null);
    if (type === 'global') list = list.filter(r => r.companyId == null);

    if (!q) return list;
    return list.filter(r =>
      r.keyword.toLowerCase().includes(q) || r.targetAccount.toLowerCase().includes(q),
    );
  });

  // ── Selección múltiple (exportar a JSON) ────────────────────────────────

  private readonly selectedIds = signal<Set<string>>(new Set());

  readonly selectedCount = computed(() => this.selectedIds().size);

  /** Todas las visibles están tildadas. Se mide contra las visibles, no contra el total: el
   *  usuario espera que "seleccionar todo" abarque lo que tiene delante, no lo que el filtro ocultó. */
  readonly allVisibleSelected = computed(() => {
    const visible = this.displayedRules();
    return visible.length > 0 && visible.every(r => this.selectedIds().has(r.id));
  });

  readonly someVisibleSelected = computed(() =>
    this.selectedCount() > 0 && !this.allVisibleSelected()
  );

  isSelected(id: string): boolean {
    return this.selectedIds().has(id);
  }

  toggleSelect(id: string, event?: Event): void {
    event?.stopPropagation();
    this.selectedIds.update(current => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  toggleSelectAllVisible(): void {
    const visible = this.displayedRules();
    this.selectedIds.set(this.allVisibleSelected() ? new Set() : new Set(visible.map(r => r.id)));
  }

  clearSelection(): void {
    this.selectedIds.set(new Set());
  }

  onExportClick(): void {
    const selected = this.selectedIds();
    this.exportRequested.emit(this.rules().filter(r => selected.has(r.id)));
  }

  // ── Menú kebab de acciones secundarias ──────────────────────────────────
  //
  // El menú se posiciona `fixed` y se renderiza fuera de la grilla, no `absolute` dentro de la
  // fila: el contenedor de la tabla tiene `overflow-auto`, que recortaría el desplegable en las
  // últimas filas —justo donde más se necesita—.

  /** Id de la regla cuyo menú está abierto; null = ninguno. */
  readonly openMenuRuleId = signal<string | null>(null);
  /** Coordenadas de viewport del menú abierto (posicionamiento fixed). */
  readonly menuPosition = signal<{ top: number; right: number } | null>(null);

  /** La regla del menú abierto, para saber qué opciones ofrecer. */
  readonly openMenuRule = computed(() => {
    const id = this.openMenuRuleId();
    return id === null ? null : this.rules().find(r => r.id === id) ?? null;
  });

  toggleMenu(rule: AccountingRule, event: MouseEvent): void {
    event.stopPropagation();

    if (this.openMenuRuleId() === rule.id) {
      this.closeMenu();
      return;
    }

    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    // Anclado al borde derecho del botón: el menú crece hacia la izquierda y nunca se sale.
    this.menuPosition.set({ top: rect.bottom + 4, right: window.innerWidth - rect.right });
    this.openMenuRuleId.set(rule.id);
  }

  closeMenu(): void {
    this.openMenuRuleId.set(null);
    this.menuPosition.set(null);
  }

  // Cualquier clic fuera cierra. El clic dentro del menú no llega acá porque lo detiene el propio
  // contenedor del desplegable.
  @HostListener('document:click')
  onDocumentClick(): void {
    this.closeMenu();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeMenu();
  }

  // La posición es de viewport: si la ventana cambia de tamaño, el menú queda flotando lejos del
  // botón que lo abrió. Cerrarlo es preferible a recalcular.
  @HostListener('window:resize')
  onResize(): void {
    this.closeMenu();
  }

  onCreate(): void {
    this.createRequested.emit();
  }

  onEdit(rule: AccountingRule): void {
    this.editRequested.emit(rule);
  }

  onDelete(rule: AccountingRule): void {
    this.deleteRequested.emit(rule);
  }

  onToggleStatus(rule: AccountingRule): void {
    this.toggleStatusRequested.emit(rule);
  }

  onPromote(rule: AccountingRule): void {
    this.promoteRequested.emit(rule);
  }

  onReapply(rule: AccountingRule): void {
    this.reapplyRequested.emit(rule);
  }

  /** Las reglas generales (de sistema o de estudio) se administran aparte, no desde acá. */
  canDelete(rule: AccountingRule): boolean {
    return rule.companyId != null;
  }

  /**
   * Alguna acción de la fila está en vuelo. Se muestra en el propio botón kebab porque, con el
   * menú cerrado, no habría ningún otro lugar donde ver que la operación sigue corriendo.
   */
  isBusy(rule: AccountingRule): boolean {
    return this.deletingId()   === rule.id
        || this.promotingId()  === rule.id
        || this.reapplyingId() === rule.id;
  }

  /** Solo las reglas propias de la empresa se pueden promover: las de estudio ya lo están. */
  canPromote(rule: AccountingRule): boolean {
    return rule.companyId != null;
  }

  /**
   * La reaplicación retroactiva es solo para reglas de empresa (decisión de alcance v1.1) y no
   * tiene sentido sobre una regla inactiva, que el motor ni siquiera evalúa.
   */
  canReapply(rule: AccountingRule): boolean {
    return rule.companyId != null && rule.isActive;
  }

  ruleScope(rule: AccountingRule): 'company' | 'studio' | 'system' {
    if (rule.companyId != null) return 'company';
    if (rule.studioTenantId != null) return 'studio';
    return 'system';
  }

  typeLabel(rule: AccountingRule): string {
    const scope = this.ruleScope(rule);
    if (scope === 'company') return 'Propia';
    if (scope === 'studio') return 'Estudio';
    return 'Sistema';
  }

  typeBadgeClass(rule: AccountingRule): string {
    const scope = this.ruleScope(rule);
    if (scope === 'company') return 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-400 dark:border-emerald-500/30';
    if (scope === 'studio') return 'bg-violet-50 text-violet-700 border-violet-200 dark:bg-violet-500/10 dark:text-violet-400 dark:border-violet-500/30';
    return 'bg-sky-50 text-sky-700 border-sky-200 dark:bg-sky-500/10 dark:text-sky-400 dark:border-sky-500/30';
  }

  evaluationBadge(rule: AccountingRule): string {
    const scope = this.ruleScope(rule);
    if (scope === 'company') return 'Prioridad Alta';
    if (scope === 'studio') return 'Prioridad Media';
    return 'Prioridad Baja';
  }

  evaluationBadgeClass(rule: AccountingRule): string {
    const scope = this.ruleScope(rule);
    if (scope === 'company') return 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-500/10 dark:text-emerald-400 dark:border-emerald-500/30';
    if (scope === 'studio') return 'bg-violet-50 text-violet-700 border-violet-200 dark:bg-violet-500/10 dark:text-violet-400 dark:border-violet-500/30';
    return 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-700 dark:text-slate-300 dark:border-slate-600';
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
