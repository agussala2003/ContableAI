import { Component, input, output, signal, computed, effect, inject } from '@angular/core';
import { DecimalPipe, NgClass } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { BankTransaction } from '../../../../core/services/transaction';
import { ChartOfAccountService } from '../../../../core/services/chart-of-account.service';
import { ToastService } from '../../../../core/services/toast.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-transaction-grid',
  standalone: true,
  imports: [DecimalPipe, FormsModule, NgClass, LucideAngularModule],
  templateUrl: './transaction-grid.html',
  styleUrl: './transaction-grid.scss',
})
export class TransactionGrid {

  transactions = input.required<BankTransaction[]>();
  sortBy       = input<string | null>(null);
  sortDir      = input<'asc' | 'desc' | null>(null);

  accountChanged   = output<{ id: string; newAccount: string }>();
  bulkAssigned     = output<{ ids: string[]; account: string }>();
  selectionChange  = output<string[]>();
  sortChange       = output<{ column: string | null; dir: 'asc' | 'desc' | null }>();
  createRuleRequested = output<void>();

  chartService  = inject(ChartOfAccountService);
  toast = inject(ToastService);
  selectedIds   = signal<Set<string>>(new Set());
  bulkAccount   = signal('');

  editingNewAccountForId  = signal<string | null>(null);
  newAccountValue         = signal('');
  creatingHeaderAccount   = signal(false);
  newHeaderAccountValue   = signal('');
  savingHeaderAccount     = signal(false);

  selectedCount  = computed(() => this.selectedIds().size);
  allSelected    = computed(() => {
    const txs = this.transactions();
    return txs.length > 0 && txs.every(t => this.selectedIds().has(t.id));
  });

  constructor() {
    // Clear selection when the transaction list changes (filter / page change)
    effect(() => {
      this.transactions();
      this.clearSelection();
    });
  }

  toggleSelect(id: string) {
    this.selectedIds.update(set => {
      const next = new Set(set);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
    this.selectionChange.emit([...this.selectedIds()]);
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

  confirmNewAccount(id: string): void {
    const name = this.newAccountValue().trim();
    if (!name) { this.cancelNewAccount(); return; }
    this.chartService.create(name).subscribe({
      next: (created) => {
        this.chartService.accountNames.update(names => [...names, created.name].sort((a, b) => a.localeCompare(b)));
        this.accountChanged.emit({ id, newAccount: created.name });
        this.editingNewAccountForId.set(null);
        this.newAccountValue.set('');
        this.toast.success(`Cuenta "${created.name}" creada con exito.`);
      },
      error: () => {
        // Si ya existe (409), igual asignamos el nombre y cerramos el input
        this.accountChanged.emit({ id, newAccount: name });
        this.editingNewAccountForId.set(null);
        this.newAccountValue.set('');
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

  openCreateRule(): void {
    this.createRuleRequested.emit();
  }
}
