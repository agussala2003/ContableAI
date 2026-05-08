import { Injectable, inject, signal, computed, effect, untracked, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { timer, switchMap, takeWhile } from 'rxjs';
import { BankTransaction, Transaction, UploadResponse, SkippedDuplicate } from '../../core/services/transaction';
import { ToastService } from '../../core/services/toast.service';
import { ConfirmDialogService } from '../../core/services/confirm-dialog.service';
import { CompanyService } from '../../core/services/company.service';
import { JournalEntryService } from '../../core/services/journal-entry.service';
import { ReconciliationFilters, ReconciliationPagination } from './models/reconciliation.models';
import { AfipService } from './afip.service';
import { RuleService } from '../../core/services/rule.service';

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
  private afipService         = inject(AfipService);
  private ruleService         = inject(RuleService);
  private readonly destroyRef = inject(DestroyRef);

  // ── Private writable state ─────────────────────────────────────────────
  private _transactions     = signal<BankTransaction[]>([]);
  private _filters          = signal<ReconciliationFilters>({
    month: null, year: null, search: '', account: '', direction: null, sortBy: null, sortDir: null, strictSearch: false, amountMode: 'exact'
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
  private _pendingAfipCount       = signal<number>(0);
  private _skippedDuplicates      = signal<SkippedDuplicate[]>([]);

  // ── Undo Stack ─────────────────────────────────────────────────────────
  private _undoStack: Array<{ id: string; oldAccount: string }> = [];


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
  readonly pendingAfipCount  = this._pendingAfipCount.asReadonly();
  readonly skippedDuplicates = this._skippedDuplicates.asReadonly();

  // ── Computed ───────────────────────────────────────────────────────────
  readonly saldo = computed(() => this._totalIngresosFiltered() - this._totalEgresosFiltered());

  readonly pendingTaxCount = computed(() =>
    this._transactions().filter(t => t.needsTaxMatching).length
  );
  readonly hasActiveFilters = computed(() => {
    const f = this._filters();
    return !!(f.search || f.month || f.year || f.account || f.direction || f.exactAmount || f.minAmount || f.maxAmount);
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
        this.refreshAfipCount();
      });
    });
  }

  clearSkippedDuplicates(): void {
    this._skippedDuplicates.set([]);
  }

  refreshAfipCount(): void {
    const companyId = this.companyService.activeCompany()?.id;
    if (!companyId) {
      this._pendingAfipCount.set(0);
      return;
    }

    this.afipService.getVouchers(companyId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (vouchers) => {
        const pending = vouchers.filter(v => !v.isMatched).length;
        this._pendingAfipCount.set(pending);
      },
      error: () => this._pendingAfipCount.set(0)
    });
  }

  // ── Init (call from page ngOnInit) ─────────────────────────────────────
  init(): void {
    this.companyService.loadCompanies().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
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
      month:        f.month    ?? undefined,
      year:         f.year     ?? undefined,
      search:       f.search   || undefined,
      account:      f.account  || undefined,
      direction:    f.direction ?? undefined,
      sortBy:       f.sortBy   ?? undefined,
      sortDir:      f.sortDir  ?? undefined,
      strictSearch: f.strictSearch || undefined,
      exactAmount:  f.amountMode === 'exact' ? f.exactAmount ?? undefined : undefined,
      minAmount:    f.amountMode === 'range' ? f.minAmount ?? undefined : undefined,
      maxAmount:    f.amountMode === 'range' ? f.maxAmount ?? undefined : undefined,
      page:         p.page,
      pageSize:     p.pageSize,
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
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
      error: () => {
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
    this._filters.update(f => ({ ...f, search: '', account: '', direction: null, month: null, year: null, strictSearch: false, exactAmount: null, minAmount: null, maxAmount: null }));
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
    const txs = this._transactions();
    const target = txs.find(t => t.id === id);
    if (!target || target.assignedAccount === newAccount) return;

    // Push to undo stack
    this._undoStack.push({ id, oldAccount: target.assignedAccount || 'Pending' });
    if (this._undoStack.length > 50) this._undoStack.shift();

    const snapshot = txs;
    // Optimistic: apply locally before API call
    this._transactions.update(txs =>
      txs.map(t => t.id === id ? { ...t, assignedAccount: newAccount } : t)
    );
    this.txService.updateTransactionAccount(id, newAccount).subscribe({
      next: ({ transaction: updated, newSuggestionKeyword }) => {
        this._transactions.update(txs =>
          txs.map(t => t.id === updated.id ? updated : t)
        );
        this.ruleService.triggerSuggestionRefresh();
        if (newSuggestionKeyword) {
          this.toast.info(`💡 Patrón detectado: se generó una sugerencia de regla para "${newSuggestionKeyword}". Revisala en Reglas.`);
        }
      },
      error: () => {
        this._transactions.set(snapshot); // rollback
        this._undoStack.pop();
        this.toast.error('Error al actualizar la transacción. Intentá de nuevo.');
      },
    });
  }

  // ── Undo last action ───────────────────────────────────────────────────
  undoLastUpdate(): void {
    const last = this._undoStack.pop();
    if (!last) {
      this.toast.warning('No hay acciones recientes para deshacer en esta vista.');
      return;
    }
    
    const { id, oldAccount } = last;
    const accountStr = oldAccount === 'Pending' ? '' : oldAccount;
    
    // Optimizamos enviando la actualización como de costumbre, la cual volverá a apilar el undo si queremos rehacer.
    // Pero como no tenemos redo explícito, solo dejamos el undo stack sin esto.
    const snapshot = this._transactions();
    this._transactions.update(txs =>
      txs.map(t => t.id === id ? { ...t, assignedAccount: accountStr } : t)
    );
    this.toast.success(`Deshaciendo última acción (cuenta revertida)`);
    
    this.txService.updateTransactionAccount(id, accountStr).subscribe({
      next: ({ transaction: updated }) => {
        this._transactions.update(txs => txs.map(t => t.id === updated.id ? updated : t));
      },
      error: () => {
        this._transactions.set(snapshot);
        // Put it back
        this._undoStack.push(last);
        this.toast.error('Error al deshacer. Intentá de nuevo.');
      }
    });
  }

  onBulkAssigned(ids: string[], account: string): void {
    this.onBulkAssignedWithOptions(ids, account);
  }

  onBulkRuleApplied(ids: string[], rule: { id: string; keyword: string; targetAccount: string }): void {
    this.onBulkAssignedWithOptions(ids, rule.targetAccount, {
      ruleId: rule.id,
      ruleKeyword: rule.keyword,
    });
  }

  private onBulkAssignedWithOptions(
    ids: string[],
    account: string,
    options?: { ruleId?: string; ruleKeyword?: string },
  ): void {
    const snapshot = this._transactions();
    const idSet = new Set(ids);
    // Optimistic: apply locally before API call
    this._transactions.update(txs =>
      txs.map(t => idSet.has(t.id) ? { ...t, assignedAccount: account } : t)
    );
    this.txService.bulkUpdate(ids, account, options?.ruleId).subscribe({
      next: (response) => {
        const updatedMap = new Map(response.transactions.map(t => [t.id, t]));
        this._transactions.update(txs => txs.map(t => updatedMap.get(t.id) ?? t));
        const n = response.updatedCount;
        if (options?.ruleKeyword) {
          this.toast.success(
            `Regla "${options.ruleKeyword}" aplicada a ${n} movimiento${n !== 1 ? 's' : ''}.`
          );
        } else {
          this.toast.success(
            `${n} movimiento${n !== 1 ? 's' : ''} actualizado${n !== 1 ? 's' : ''} a "${response.assignedAccount}".`
          );
          this.ruleService.triggerSuggestionRefresh();
        }
      },
      error: () => {
        this._transactions.set(snapshot); // rollback
        this.toast.error(options?.ruleId
          ? 'Error al aplicar la regla en forma masiva.'
          : 'Error al aplicar la acción masiva.');
      },
    });
  }

  // ── File upload ────────────────────────────────────────────────────────
  uploadFiles(
    event: { files: File[]; bankCode: string; companyId?: string; withoutDateFilter: boolean; forceReapplyRules?: boolean },
    onSuccess?: () => void,
  ): void {
    this._isLoading.set(true);
    const companyId = event.companyId ?? this.companyService.activeCompany()?.id;

    this.txService.uploadFiles(event.files, event.bankCode, companyId, event.withoutDateFilter, event.forceReapplyRules).subscribe({
      next: (response) => {
        const generated = response.totalProcessed > 0;
        const reapplied = (response.reappliedToExisting ?? 0) > 0;

        if (event.withoutDateFilter && generated) {
          this._filters.update(ff => ({ ...ff, month: null, year: null }));
        }
        else if (generated) {
          this._adjustFiltersForImport(response);
        }
        
        if (generated || reapplied) {
          this._pagination.update(p => ({ ...p, page: 1 }));
          this.loadData(); // sets _isLoading=true internally, clears it when done
          onSuccess?.();
          
          const filesInfo = response.totalFiles > 1 ? ` (${response.totalFiles} archivos)` : '';
          if (generated) {
            this.toast.success(
              `¡Éxito${filesInfo}! Se procesaron ${response.totalProcessed} movimientos` +
              `${response.companyName ? ' para ' + response.companyName : ''}. ` +
              `(${response.duplicatesSkipped} duplicados omitidos)`
            );
            if (response.skippedDuplicates?.length) {
              this._skippedDuplicates.set(response.skippedDuplicates);
            }
          }
          if (reapplied) {
            this.toast.success(`Se aplicaron tus reglas actualizadas a ${response.reappliedToExisting} transacciones existentes.`);
          }
        } 
        else if (response.duplicatesSkipped > 0 && !event.forceReapplyRules) {
          this._isLoading.set(false);
          this.confirmDialog.confirm({
            title: 'Extracto ya subido',
            message: `Se detectaron ${response.duplicatesSkipped} movimientos que ya estaban cargados. ¿Querés re-aplicar tus reglas actuales sobre esos movimientos pendientes?`,
            confirmLabel: 'Sí, re-aplicar reglas'
          }).then(ok => {
            if (ok) {
              this.uploadFiles({ ...event, forceReapplyRules: true }, onSuccess);
            }
          });
        } 
        else if (response.duplicatesSkipped > 0) {
          this._isLoading.set(false);
          this.toast.warning(
            `No se agregaron movimientos nuevos, ni hubo cambios que requieran reclasificar. ${response.duplicatesSkipped} transacciones ya estaban cargadas y siguen igual.`
          );
        } 
        else {
          this._isLoading.set(false);
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
        this.toast.success(res.message || 'Generación de asientos iniciada en segundo plano. Esto puede demorar unos minutos.');
        
        if (res.jobId) {
          timer(3000, 3000).pipe(
            switchMap(() => this.journalEntryService.getJobStatus(res.jobId!)),
            takeWhile(status => status.state === 'Processing' || status.state === 'Enqueued', true),
            takeUntilDestroyed(this.destroyRef),
          ).subscribe({
            next: (status) => {
              if (status.state === 'Succeeded') {
                this.toast.success('¡Asientos generados correctamente!');
                this.loadData();
              } else if (status.state === 'Failed') {
                this.toast.error('La generación de asientos falló. Revisa los logs del servidor.');
              }
            },
            error: () => this.toast.error('Error al monitorear el estado de la generación de asientos.'),
          });
        } else {
          this.loadData();
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

  // ── Import filter adjustment ────────────────────────────────────────────
  private _adjustFiltersForImport(response: UploadResponse): void {
    const f = this._filters();
    if (!f.month && !f.year) return; // No active period filter — all data visible

    const imported = response.perFile.flatMap(pf => pf.transactions);
    if (!imported.length) return;

    // Check if any imported transaction falls within the current filter
    const anyVisible = imported.some(t => {
      const [txYear, txMonth] = t.date.split('-').map(Number);
      return (!f.month || txMonth === f.month) && (!f.year || txYear === f.year);
    });
    if (anyVisible) return;

    // Tally month+year occurrences to find the dominant period
    const counts = new Map<string, { month: number; year: number; count: number }>();
    for (const t of imported) {
      const [txYear, txMonth] = t.date.split('-').map(Number);
      const key = `${txYear}-${txMonth}`;
      const entry = counts.get(key) ?? { month: txMonth, year: txYear, count: 0 };
      entry.count++;
      counts.set(key, entry);
    }
    const dominant = [...counts.values()].sort((a, b) => b.count - a.count)[0];

    this._filters.update(ff => ({ ...ff, month: dominant.month, year: dominant.year }));

    const MONTHS = ['Enero','Febrero','Marzo','Abril','Mayo','Junio','Julio','Agosto','Septiembre','Octubre','Noviembre','Diciembre'];
    this.toast.warning(
      `Filtro actualizado a ${MONTHS[dominant.month - 1]} ${dominant.year} para mostrar los movimientos importados.`
    );
  }
}
