import { Injectable, inject, signal, computed, effect, untracked } from '@angular/core';
import { BankTransaction, Transaction } from '../../core/services/transaction';
import { ToastService } from '../../core/services/toast.service';
import { ConfirmDialogService } from '../../core/services/confirm-dialog.service';
import { CompanyService } from '../../core/services/company.service';
import { JournalEntryService } from '../../core/services/journal-entry.service';
import { ReconciliationFilters, ReconciliationPagination } from './models/reconciliation.models';

/**
 * Feature-scoped state service for the reconciliation module.
 * Provided in ReconciliationPage (providers: [ReconciliationService]) so its
 * lifetime is tied to the page component.
 *
 * Exposes only readonly Signals to the outside world.
 */
@Injectable()
export class ReconciliationService {
  private txService           = inject(Transaction);
  private toast               = inject(ToastService);
  private confirmDialog       = inject(ConfirmDialogService);
  private companyService      = inject(CompanyService);
  private journalEntryService = inject(JournalEntryService);

  // ── Private writable state ─────────────────────────────────────────────
  private _transactions     = signal<BankTransaction[]>([]);
  private _filters          = signal<ReconciliationFilters>({
    month: null, year: null, search: '', account: '', sortBy: null, sortDir: null,
  });
  private _pagination       = signal<ReconciliationPagination>({
    page: 1, pageSize: 10, totalCount: 0, totalPages: 0,
  });
  private _isLoading              = signal(false);
  private _isGenerating           = signal(false);
  private _totalIngresosFiltered  = signal(0);
  private _totalEgresosFiltered   = signal(0);
  private _totalIngresosAll       = signal(0);
  private _totalEgresosAll        = signal(0);
  private _availableAccounts      = signal<string[]>([]);
  private _availableMonths        = signal<number[]>([]);
  private _availableYears         = signal<number[]>([]);

  // ── Public readonly API ────────────────────────────────────────────────
  readonly transactions     = this._transactions.asReadonly();
  readonly filters          = this._filters.asReadonly();
  readonly pagination       = this._pagination.asReadonly();
  readonly isLoading        = this._isLoading.asReadonly();
  readonly isGenerating     = this._isGenerating.asReadonly();
  readonly totalIngresos    = this._totalIngresosFiltered.asReadonly();
  readonly totalEgresos     = this._totalEgresosFiltered.asReadonly();
  readonly totalIngresosAll = this._totalIngresosAll.asReadonly();
  readonly totalEgresosAll  = this._totalEgresosAll.asReadonly();
  readonly availableAccounts = this._availableAccounts.asReadonly();
  readonly availableMonths   = this._availableMonths.asReadonly();
  readonly availableYears    = this._availableYears.asReadonly();

  // ── Computed ───────────────────────────────────────────────────────────
  readonly saldo = computed(() => this._totalIngresosFiltered() - this._totalEgresosFiltered());

  readonly pendingTaxCount = computed(() =>
    this._transactions().filter(t => t.needsTaxMatching).length
  );
  readonly hasActiveFilters = computed(() => {
    const f = this._filters();
    return !!(f.search || f.month || f.year || f.account);
  });
  readonly eligibleIds = computed(() =>
    this._transactions()
      .filter(t => t.assignedAccount && !t.journalEntryId)
      .map(t => t.id)
  );
  readonly canExport = computed(() => {
    const companyId = this.companyService.activeCompany()?.id;
    return !!companyId && !this._isLoading() && this._pagination().totalCount > 0;
  });

  constructor() {
    // Reload when the active company changes (reset to page 1)
    effect(() => {
      this.companyService.activeCompany();
      untracked(() => {
        this._pagination.update(p => ({ ...p, page: 1 }));
        this.loadData();
      });
    });
  }

  // ── Init (call from page ngOnInit) ─────────────────────────────────────
  init(): void {
    this.companyService.loadCompanies().subscribe({
      error: () => this.loadData(),
    });
  }

  // ── Data loading ───────────────────────────────────────────────────────
  loadData(): void {
    const f = this._filters();
    const p = this._pagination();
    const companyId = this.companyService.activeCompany()?.id;

    this._isLoading.set(true);
    this.txService.getTransactions({
      companyId,
      month:    f.month    ?? undefined,
      year:     f.year     ?? undefined,
      search:   f.search   || undefined,
      account:  f.account  || undefined,
      sortBy:   f.sortBy   ?? undefined,
      sortDir:  f.sortDir  ?? undefined,
      page:     p.page,
      pageSize: p.pageSize,
    }).subscribe({
      next: (result) => {
        this._transactions.set(result.items);
        this._pagination.update(pg => ({
          ...pg,
          totalCount: result.totalCount,
          totalPages: result.totalPages,
        }));
        this._totalIngresosFiltered.set(result.totalIngresosFiltered ?? 0);
        this._totalEgresosFiltered.set(result.totalEgresosFiltered ?? 0);
        this._totalIngresosAll.set(result.totalIngresosAll ?? 0);
        this._totalEgresosAll.set(result.totalEgresosAll ?? 0);
        this._availableAccounts.set(result.availableAccounts ?? []);
        this._availableMonths.set(result.availableMonths ?? []);
        this._availableYears.set(result.availableYears ?? []);
        this._isLoading.set(false);
      },
      error: (err) => {
        console.error('Error cargando datos:', err);
        this._isLoading.set(false);
      },
    });
  }

  // ── Filters ────────────────────────────────────────────────────────────
  /** Updates one or more filter fields without triggering a reload. */
  setFilter(patch: Partial<ReconciliationFilters>): void {
    this._filters.update(f => ({ ...f, ...patch }));
  }

  /** Resets page to 1 and reloads. Call after setting filters when ready. */
  applyFilters(): void {
    this._pagination.update(p => ({ ...p, page: 1 }));
    this.loadData();
  }

  applySort(sortBy: string | null, sortDir: 'asc' | 'desc' | null): void {
    this._filters.update(f => ({ ...f, sortBy, sortDir }));
    this._pagination.update(p => ({ ...p, page: 1 }));
    this.loadData();
  }

  clearFilters(): void {
    this._filters.update(f => ({ ...f, search: '', account: '', month: null, year: null }));
    this._pagination.update(p => ({ ...p, page: 1 }));
    this.loadData();
  }

  // ── Pagination ─────────────────────────────────────────────────────────
  changePage(page: number): void {
    this._pagination.update(p => ({ ...p, page }));
    this.loadData();
  }

  setPageSize(pageSize: number): void {
    const normalized = Math.max(1, Math.min(500, pageSize));
    this._pagination.update(p => ({ ...p, page: 1, pageSize: normalized }));
    this.loadData();
  }

  getPagesArray(): number[] {
    const { totalPages, page } = this._pagination();
    const delta = 2;
    const pages: number[] = [];
    for (let i = Math.max(1, page - delta); i <= Math.min(totalPages, page + delta); i++) {
      pages.push(i);
    }
    return pages;
  }

  // ── Transaction updates (optimistic) ──────────────────────────────────
  updateTransaction(id: string, newAccount: string): void {
    const snapshot = this._transactions();
    // Optimistic: apply locally before API call
    this._transactions.update(txs =>
      txs.map(t => t.id === id ? { ...t, assignedAccount: newAccount } : t)
    );
    this.txService.updateTransactionAccount(id, newAccount).subscribe({
      next: (updated) => {
        // Reconcile with server response
        this._transactions.update(txs =>
          txs.map(t => t.id === updated.id ? updated : t)
        );
      },
      error: () => {
        this._transactions.set(snapshot); // rollback
        this.toast.error('Error al actualizar la transacción. Intentá de nuevo.');
      },
    });
  }

  onBulkAssigned(ids: string[], account: string): void {
    const snapshot = this._transactions();
    const idSet = new Set(ids);
    // Optimistic: apply locally before API call
    this._transactions.update(txs =>
      txs.map(t => idSet.has(t.id) ? { ...t, assignedAccount: account } : t)
    );
    this.txService.bulkUpdate(ids, account).subscribe({
      next: (response) => {
        const updatedMap = new Map(response.transactions.map(t => [t.id, t]));
        this._transactions.update(txs => txs.map(t => updatedMap.get(t.id) ?? t));
        const n = response.updatedCount;
        this.toast.success(
          `${n} movimiento${n !== 1 ? 's' : ''} actualizado${n !== 1 ? 's' : ''} a "${response.assignedAccount}".`
        );
      },
      error: () => {
        this._transactions.set(snapshot); // rollback
        this.toast.error('Error al aplicar la acción masiva.');
      },
    });
  }

  // ── File upload ────────────────────────────────────────────────────────
  uploadFiles(
    event: { files: File[]; bankCode: string; companyId?: string },
    onSuccess?: () => void,
  ): void {
    this._isLoading.set(true);
    const companyId = event.companyId ?? this.companyService.activeCompany()?.id;

    this.txService.uploadFiles(event.files, event.bankCode, companyId).subscribe({
      next: (response) => {
        this._pagination.update(p => ({ ...p, page: 1 }));
        this.loadData(); // sets _isLoading=true internally, clears it when done

        if (response.totalProcessed > 0) {
          onSuccess?.();
          const filesInfo = response.totalFiles > 1 ? ` (${response.totalFiles} archivos)` : '';
          this.toast.success(
            `¡Éxito${filesInfo}! Se procesaron ${response.totalProcessed} movimientos` +
            `${response.companyName ? ' para ' + response.companyName : ''}. ` +
            `(${response.duplicatesSkipped} duplicados omitidos)`,
          );
        } else if (response.duplicatesSkipped > 0) {
          this.toast.warning(
            `No se agregaron movimientos nuevos. ${response.duplicatesSkipped} transacciones ya estaban cargadas.`
          );
        } else {
          this.toast.warning('No se encontraron movimientos para importar.');
        }
      },
      error: () => {
        this._isLoading.set(false);
        this.toast.error('Error de conexión con el servidor. Intentá de nuevo.');
      },
    });
  }

  // ── Delete all ─────────────────────────────────────────────────────────
  async clearAll(): Promise<void> {
    const ok = await this.confirmDialog.confirm({
      title:        '¿Borrar toda la grilla?',
      message:      'Esta acción eliminará TODOS los movimientos y no se puede deshacer.',
      confirmLabel: 'Sí, borrar todo',
    });
    if (!ok) return;

    this.txService.deleteAllTransactions().subscribe({
      next: () => {
        this._transactions.set([]);
        this._pagination.update(p => ({ ...p, page: 1, totalCount: 0, totalPages: 0 }));
        this._filters.update(f => ({ ...f, search: '', account: '', month: null, year: null }));
        this._totalIngresosFiltered.set(0);
        this._totalEgresosFiltered.set(0);
        this._totalIngresosAll.set(0);
        this._totalEgresosAll.set(0);
        this._availableAccounts.set([]);
        this._availableMonths.set([]);
        this._availableYears.set([]);
        this.toast.success('La grilla se vacíó correctamente.');
      },
      error: () => this.toast.error('Hubo un error al limpiar la grilla.'),
    });
  }

  // ── CSV export ─────────────────────────────────────────────────────────
  downloadCsv(): void {
    const companyId = this.companyService.activeCompany()?.id;
    const { month, year } = this._filters();

    this.txService.downloadCsv(companyId, month ?? undefined, year ?? undefined).subscribe({
      error: (err) => {
        if (err?.status === 404) {
          const periodo = month && year ? `${String(month).padStart(2, '0')}/${year}`
                        : month         ? `mes ${month}`
                        : year          ? `año ${year}`
                        : 'el período seleccionado';
          this.toast.warning(
            `No hay transacciones para exportar en ${periodo}. Probá cambiando el filtro de mes/año.`
          );
        } else {
          this.toast.error('Error al generar el CSV. Intentá de nuevo.');
        }
      },
    });
  }

  // ── Journal entry generation ───────────────────────────────────────────
  generateEntries(selectedIds: string[]): void {
    const eligibleSet = new Set(this.eligibleIds());

    if (selectedIds.length > 0) {
      const ids = selectedIds.filter(id => eligibleSet.has(id));
      if (ids.length === 0) {
        this.toast.warning('Las filas seleccionadas ya están asentadas o sin cuenta asignada.');
        return;
      }
      this._doGenerate(ids);
    } else {
      const companyId = this.companyService.activeCompany()?.id;
      this._isGenerating.set(true);
      this.txService.getUnbookedIds(companyId).subscribe({
        next: (allIds) => {
          if (allIds.length === 0) {
            this._isGenerating.set(false);
            this.toast.warning('No hay movimientos sin asentar para esta empresa.');
            return;
          }
          this._doGenerate(allIds);
        },
        error: () => {
          this._isGenerating.set(false);
          this.toast.error('Error al obtener los movimientos pendientes.');
        },
      });
    }
  }

  private _doGenerate(ids: string[]): void {
    this._isGenerating.set(true);
    this.journalEntryService.generate(ids).subscribe({
      next: (res) => {
        this._isGenerating.set(false);

        const generated = res.generated ?? 0;
        const duplicatesSkipped = res.duplicatesSkipped ?? 0;
        const entries = res.entries ?? [];
        const linkedTransactions = res.linkedTransactions ?? [];

        const entryMap = new Map(entries.map(e => [e.bankTransactionId, e.id]));
        const linkedSet = new Set(linkedTransactions.map(l => l.transactionId));
        for (const linked of linkedTransactions) {
          entryMap.set(linked.transactionId, linked.journalEntryId);
        }

        if (entryMap.size > 0) {
          this._transactions.update(txs =>
            txs.map(t => {
              if (!entryMap.has(t.id)) return t;
              return {
                ...t,
                journalEntryId: entryMap.get(t.id)!,
                isPossibleDuplicate: t.isPossibleDuplicate || linkedSet.has(t.id),
              };
            })
          );
        }

        if (generated === 0) {
          if (duplicatesSkipped > 0) {
            this.toast.success(
              `${duplicatesSkipped} movimiento${duplicatesSkipped !== 1 ? 's' : ''} ya tenía${duplicatesSkipped !== 1 ? 'n' : ''} un asiento equivalente y queda${duplicatesSkipped !== 1 ? 'ron' : ''} marcado${duplicatesSkipped !== 1 ? 's' : ''} como asentado${duplicatesSkipped !== 1 ? 's' : ''}.`
            );
          } else {
            this.toast.warning(
              res.message?.trim()
                ? res.message
                : 'No se generaron asientos. Verificá que las transacciones tengan cuenta asignada.'
            );
          }
          return;
        }

        const n = generated;
        if (duplicatesSkipped > 0) {
          this.toast.success(
            `${n} asiento${n !== 1 ? 's' : ''} generado${n !== 1 ? 's' : ''} correctamente. ${duplicatesSkipped} movimiento${duplicatesSkipped !== 1 ? 's' : ''} adicional${duplicatesSkipped !== 1 ? 'es' : ''} ya estaba${duplicatesSkipped !== 1 ? 'n' : ''} asentado${duplicatesSkipped !== 1 ? 's' : ''} y se marcó${duplicatesSkipped !== 1 ? 'ron' : ''} en la grilla.`
          );
        } else {
          this.toast.success(`${n} asiento${n !== 1 ? 's' : ''} generado${n !== 1 ? 's' : ''} correctamente.`);
        }
      },
      error: (err) => {
        this._isGenerating.set(false);
        const detail: string = err?.error?.detail ?? err?.error?.title ?? null;
        this.toast.error(detail ?? 'Error al generar los asientos contables.');
      },
    });
  }

  // ── AFIP ───────────────────────────────────────────────────────────────
  onAfipMatchComplete(): void {
    this.loadData();
  }
}
