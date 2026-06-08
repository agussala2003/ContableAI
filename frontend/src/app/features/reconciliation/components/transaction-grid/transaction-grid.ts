import { Component, input, output, signal, computed, effect, untracked, inject, HostListener } from '@angular/core';
import { DecimalPipe, NgClass } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { BankTransaction } from '../../../../core/services/transaction';
import { ChartOfAccountService } from '../../../../core/services/chart-of-account.service';
import { ToastService } from '../../../../core/services/toast.service';
import { LucideAngularModule } from 'lucide-angular';
import { AccountCombobox } from '../../../../shared/components/account-combobox/account-combobox';

@Component({
  selector: 'app-transaction-grid',
  standalone: true,
  imports: [DecimalPipe, FormsModule, NgClass, LucideAngularModule, AccountCombobox],
  templateUrl: './transaction-grid.html',
  styleUrl: './transaction-grid.scss',
})
export class TransactionGrid {

  transactions = input.required<BankTransaction[]>();
  sortBy       = input<string | null>(null);
  sortDir      = input<'asc' | 'desc' | null>(null);

  accountChanged   = output<{ id: string; newAccount: string }>();
  bulkAssigned     = output<{ ids: string[]; account: string }>();
  applyRuleRequested = output<string[]>();
  selectionChange  = output<string[]>();
  sortChange       = output<{ column: string | null; dir: 'asc' | 'desc' | null }>();
  // Emit payload with description and optional account for pre-filling rule creation
  createRuleRequested = output<{ description: string; account?: string }>();
  undoRequested    = output<void>();

  chartService  = inject(ChartOfAccountService);
  toast         = inject(ToastService);
  router        = inject(Router);
  selectedIds   = signal<Set<string>>(new Set());
  bulkAccount   = signal('');

  // Estilo del input de asignación masiva (combobox), alineado al resto de la toolbar.
  readonly bulkInputClass =
    'w-full h-7 pl-2.5 pr-9 rounded-md border border-indigo-200 dark:border-indigo-700/50 ' +
    'bg-white dark:bg-slate-900 text-xs text-slate-800 dark:text-slate-200 ' +
    'placeholder:text-slate-400 dark:placeholder:text-slate-500 ' +
    'focus:outline-none focus:ring-1 focus:ring-indigo-500 focus:border-indigo-500 transition-colors';

  lastClickedId = signal<string | null>(null);

  editingNewAccountForId  = signal<string | null>(null);
  newAccountValue         = signal('');
  creatingHeaderAccount   = signal(false);
  newHeaderAccountValue   = signal('');
  savingHeaderAccount     = signal(false);
  inlineEditingId         = signal<string | null>(null);

  bulkAccountValid = computed(() => {
    const account = this.bulkAccount().trim();
    return account.length > 0 && this.chartService.accountNames().includes(account);
  });

  selectedCount  = computed(() => this.selectedIds().size);
  allSelected    = computed(() => {
    const txs = this.transactions();
    return txs.length > 0 && txs.every(t => this.selectedIds().has(t.id));
  });
  someSelected   = computed(() => this.selectedIds().size > 0 && !this.allSelected());

  constructor() {
    // Remove from selection any IDs that are no longer in the current page.
    // This clears selection on filter/page change (new IDs) but preserves it
    // during in-place optimistic updates (same IDs, mutated properties).
    effect(() => {
      const txs = this.transactions();
      const validIds = new Set(txs.map(t => t.id));
      untracked(() => {
        const current = this.selectedIds();
        if (current.size === 0) return;
        const filtered = new Set([...current].filter(id => validIds.has(id)));
        if (filtered.size !== current.size) {
          this.selectedIds.set(filtered);
          this.bulkAccount.set('');
          this.selectionChange.emit([...filtered]);
        }
      });
    });
  }

  toggleSelect(id: string) {
    this.selectedIds.update(set => {
      const next = new Set(set);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
    this.lastClickedId.set(id);
    this.selectionChange.emit([...this.selectedIds()]);
  }

  // Handles clicks on row checkboxes. stopPropagation prevents the click from
  // bubbling to <tr> (onRowClick), which would double-toggle with Ctrl+click.
  onCheckboxClick(e: MouseEvent, id: string): void {
    e.stopPropagation();
    if (e.shiftKey && this.lastClickedId()) {
      document.getSelection()?.removeAllRanges();
      const txs = this.transactions();
      const start = txs.findIndex(t => t.id === this.lastClickedId());
      const end   = txs.findIndex(t => t.id === id);
      if (start !== -1 && end !== -1) {
        const min = Math.min(start, end);
        const max = Math.max(start, end);
        const next = new Set(this.selectedIds());
        for (let i = min; i <= max; i++) next.add(txs[i].id);
        this.selectedIds.set(next);
        this.selectionChange.emit([...next]);
      }
      this.lastClickedId.set(id);
    } else {
      this.toggleSelect(id);
    }
  }

  onRowClick(e: MouseEvent, id: string): void {
    if (e.shiftKey && this.lastClickedId()) {
      document.getSelection()?.removeAllRanges();
      const txs = this.transactions();
      const start = txs.findIndex(t => t.id === this.lastClickedId());
      const end   = txs.findIndex(t => t.id === id);
      if (start !== -1 && end !== -1) {
        const min = Math.min(start, end);
        const max = Math.max(start, end);
        const newSelection = new Set(this.selectedIds());
        for (let i = min; i <= max; i++) newSelection.add(txs[i].id);
        this.selectedIds.set(newSelection);
        this.selectionChange.emit([...newSelection]);
      }
    } else if (e.metaKey || e.ctrlKey) {
      this.toggleSelect(id);
    }
    this.lastClickedId.set(id);
  }

  toggleSelectAll() {
    if (this.allSelected()) {
      this.clearSelection();
    } else {
      this.selectedIds.set(new Set(this.transactions().map(t => t.id)));
      this.selectionChange.emit([...this.selectedIds()]);
    }
  }

  /** Selecciona un conjunto específico de IDs desde el exterior. */
  setSelection(ids: string[]) {
    this.selectedIds.set(new Set(ids));
    this.bulkAccount.set('');
    this.selectionChange.emit(ids);
  }

  clearSelection() {
    this.selectedIds.set(new Set());
    this.bulkAccount.set('');
    this.selectionChange.emit([]);
  }

  isSelected(id: string): boolean {
    return this.selectedIds().has(id);
  }

  /** Devuelve las clases CSS del indicador semáforo según confidenceScore. */
  semaforoClass(tx: BankTransaction): string {
    if (tx.needsTaxMatching) return 'bg-amber-400 dark:bg-amber-500';
    const s = tx.confidenceScore ?? 0;
    if (s >= 0.9)  return 'bg-emerald-500 dark:bg-emerald-400';
    if (s >= 0.5)  return 'bg-amber-400 dark:bg-amber-500';
    return 'bg-red-500 dark:bg-red-400';
  }

  /** Devuelve el tooltip del semáforo. */
  semaforoTitle(tx: BankTransaction): string {
    if (tx.needsTaxMatching) return 'Requiere identificación impositiva';
    const s = tx.confidenceScore ?? 0;
    if (s >= 0.9)  return `Alta confianza (${Math.round(s * 100)}%)`;
    if (s >= 0.5)  return `Revisar (${Math.round(s * 100)}%)`;
    return 'Requiere atención manual';
  }

  applyBulk() {
    const account = this.bulkAccount().trim();
    if (!account || this.selectedIds().size === 0) return;
    this.bulkAssigned.emit({ ids: [...this.selectedIds()], account });
    this.clearSelection();
  }

  openApplyRuleModal(): void {
    if (this.selectedIds().size === 0) return;
    this.applyRuleRequested.emit([...this.selectedIds()]);
  }

  saveHeaderAccount(): void {
    const name = this.newHeaderAccountValue().trim();
    if (!name || this.savingHeaderAccount()) return;
    this.savingHeaderAccount.set(true);
    this.chartService.create(name).subscribe({
      next: (created) => {
        this.chartService.accountNames.update(names => [...names, created.name].sort((a, b) => a.localeCompare(b)));
        this.creatingHeaderAccount.set(false);
        this.newHeaderAccountValue.set('');
        this.savingHeaderAccount.set(false);
        this.toast.success(`Cuenta "${created.name}" creada con exito.`);
      },
      error: () => {
        this.savingHeaderAccount.set(false);
        this.toast.error('No se pudo crear la cuenta contable.');
      },
    });
  }

  onAccountChange(id: string, value: string): void {
    if (value === '__new__') {
      this.editingNewAccountForId.set(id);
      this.newAccountValue.set('');
    } else {
      this.accountChanged.emit({ id, newAccount: value });
    }
  }

  onAccountInput(id: string, event: Event): void {
    const input = event.target as HTMLInputElement;
    const value = input.value.trim();
    
    if (value === '__new__') {
       this.editingNewAccountForId.set(id);
       this.newAccountValue.set('');
       return;
    }
    if (!value) {
       this.accountChanged.emit({ id, newAccount: 'Pending' });
       return;
    }
    
    // Check if exactly in the list (case insensitive lookup)
    const exists = this.chartService.accountNames().find(a => a.toLowerCase() === value.toLowerCase());
    if (exists) {
       this.accountChanged.emit({ id, newAccount: exists });
       input.value = exists; // force exact casing display
       this.inlineEditingId.set(null);
    } else {
       // Open the "New Account" inline editor prefilled
       this.editingNewAccountForId.set(id);
       this.newAccountValue.set(value);
       this.inlineEditingId.set(null);
    }
  }

  startInlineEdit(id: string, event?: Event): void {
    if (event) event.stopPropagation();
    this.inlineEditingId.set(id);
    // Focus the input in the next tick
    setTimeout(() => {
      const el = document.getElementById('grid-input-inline-' + id);
      if (el) {
        (el as HTMLInputElement).focus();
        (el as HTMLInputElement).select();
      }
    }, 50);
  }

  cancelInlineEdit(id: string): void {
    if (this.inlineEditingId() === id) {
      this.inlineEditingId.set(null);
    }
  }

  onInputKeyDown(e: KeyboardEvent, index: number) {
     if (e.key === 'Enter') {
         e.preventDefault();
         const target = e.target as HTMLInputElement;
         target.blur(); // Triggers "change" event which fires onAccountInput
         
         // Move to next row
         setTimeout(() => {
           const txs = this.transactions();
           if (index + 1 < txs.length) {
              const nextId = txs[index + 1].id;
              this.startInlineEdit(nextId);
           }
         }, 50);
     }
  }

  @HostListener('window:keydown', ['$event'])
  handleGlobalHotkey(e: KeyboardEvent) {
    // Modo Excel: Undo general en la ventana entera
    if ((e.metaKey || e.ctrlKey) && e.key === 'z') {
      // Ignorar si el usuario está en un input normal ajeno a la grid, excepto el listado de auto-complete
      const target = e.target as HTMLElement;
      if (target.tagName === 'INPUT' && !target.id.startsWith('grid-input')) return;
      if (target.tagName === 'TEXTAREA') return;

      e.preventDefault();
      this.undoRequested.emit();
    }
  }

  confirmNewAccount(id: string): void {
    const name = this.newAccountValue().trim();
    if (!name) { this.cancelNewAccount(); return; }
    this.chartService.create(name).subscribe({
      next: (created) => {
        this.chartService.accountNames.update(names => [...names, created.name].sort((a, b) => a.localeCompare(b)));
        this.accountChanged.emit({ id, newAccount: created.name });
        this.newAccountValue.set('');
        this.editingNewAccountForId.set(null);
        this.toast.success(`Cuenta "${created.name}" creada con exito.`);
      },
      error: () => {
        // Si ya existe (409), igual asignamos el nombre y cerramos el input
        this.accountChanged.emit({ id, newAccount: name });
        this.newAccountValue.set('');
        this.editingNewAccountForId.set(null);
        this.toast.warning('La cuenta ya existia o no se pudo crear; se intento asignar el nombre ingresado.');
      },
    });
  }

  cancelNewAccount(): void {
    this.editingNewAccountForId.set(null);
    this.newAccountValue.set('');
  }

  onColumnSort(column: string) {
    const currentDir = this.sortDir();
    const currentCol = this.sortBy();
    if (currentCol !== column) {
      this.sortChange.emit({ column, dir: 'asc' });
    } else if (currentDir === 'asc') {
      this.sortChange.emit({ column, dir: 'desc' });
    } else if (currentDir === 'desc') {
      this.sortChange.emit({ column: null, dir: null });
    } else {
      this.sortChange.emit({ column, dir: 'asc' });
    }
  }

  // Opens quick rule modal pre-filled from first selected transaction
  openCreateRuleFromSelection(): void {
    if (this.selectedIds().size === 0) {
      // No selection, fallback to generic empty rule creation
      this.createRuleRequested.emit({ description: '', account: undefined });
      return;
    }
    // Find first selected transaction data
    const txs = this.transactions();
    const firstId = this.selectedIds().values().next().value;
    const tx = txs.find(t => t.id === firstId);
    const desc = tx?.description ?? '';
    const account = tx?.assignedAccount && tx.assignedAccount !== 'Pending' ? tx.assignedAccount : undefined;
    this.createRuleRequested.emit({ description: desc, account });
  }

  // Legacy method kept for compatibility
  openCreateRule(): void {
    this.createRuleRequested.emit({ description: '', account: undefined });
  }

  navigateToAccounts(): void {
    this.router.navigate(['/chart-of-accounts']);
  }
}
