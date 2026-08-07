import { Injectable, inject, signal, computed, effect, untracked, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { timer, switchMap, takeWhile, take, tap } from 'rxjs';
import { BankTransaction, Transaction, UploadResponse, UploadJobResultEnvelope, SkippedDuplicate, CurrencyTotals } from '../../core/services/transaction';
import { ToastService } from '../../core/services/toast.service';
import { ConfirmDialogService } from '../../core/services/confirm-dialog.service';
import { CompanyService } from '../../core/services/company.service';
import { JournalEntryService } from '../../core/services/journal-entry.service';
import { BankAccountFilterOption, ReconciliationFilters, ReconciliationPagination } from './models/reconciliation.models';
import { BankAccountService } from '../../core/services/bank-account.service';
import { AfipService } from './afip.service';
import { RuleService } from '../../core/services/rule.service';

/** Payload que emite la Dropzone (y que el reintento con reglas re-emite tal cual). */
export interface UploadEvent {
  files: File[];
  bankCode: string;
  companyId?: string;
  withoutDateFilter: boolean;
  forceReapplyRules?: boolean;
  /** Cuenta elegida a mano; ausente = detectar por OCR. */
  bankAccountId?: string;
}

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
  private bankAccountService  = inject(BankAccountService);
  private readonly destroyRef = inject(DestroyRef);

  // ── Private writable state ─────────────────────────────────────────────
  private _transactions     = signal<BankTransaction[]>([]);
  private _filters          = signal<ReconciliationFilters>({
    month: null, year: null, search: '', account: '', direction: null, currency: null, sortBy: null, sortDir: null, strictSearch: false, amountMode: 'exact', bankAccountId: null
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
  private _currencyTotals         = signal<CurrencyTotals[]>([]);
  private _availableAccounts      = signal<string[]>([]);
  private _availableBankAccounts  = signal<BankAccountFilterOption[]>([]);
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
  readonly currencyTotals   = this._currencyTotals.asReadonly();
  /** True cuando el conjunto filtrado tiene más de una moneda: la UI separa totales por moneda. */
  readonly isMultiCurrency  = computed(() => this._currencyTotals().length > 1);
  readonly availableAccounts = this._availableAccounts.asReadonly();
  readonly availableBankAccounts = this._availableBankAccounts.asReadonly();

  /**
   * La columna "Cuenta bancaria" solo aporta cuando la grilla mezcla cuentas. Con el filtro puesto
   * en una sola, repetiría el mismo valor en todas las filas; y si la empresa no tiene más de una
   * cuenta en los datos, no hay nada que distinguir.
   */
  readonly showBankAccountColumn = computed(() =>
    this._filters().bankAccountId === null && this._availableBankAccounts().length > 1
  );
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
    return !!(f.search || f.month || f.year || f.account || f.bankAccountId || f.direction || f.currency || f.exactAmount || f.minAmount || f.maxAmount);
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
      const company = this.companyService.activeCompany();
      untracked(() => {
        this._pagination.update(p => ({ ...p, page: 1 }));
        this._filters.update(f => ({ ...f, bankAccountId: null }));
        this.loadData();
        this.refreshAfipCount();
        // Alimenta el selector de cuenta de la Dropzone.
        this.bankAccountService.refresh(company?.id);
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
      bankAccountId: f.bankAccountId ?? undefined,
      direction:    f.direction ?? undefined,
      currency:     f.currency  ?? undefined,
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
        this._currencyTotals.set(result.currencyTotals ?? []);
        this._availableAccounts.set(result.availableAccounts ?? []);
        this._availableBankAccounts.set(result.availableBankAccounts ?? []);
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
    this._filters.update(f => ({ ...f, search: '', account: '', bankAccountId: null, direction: null, currency: null, month: null, year: null, strictSearch: false, exactAmount: null, minAmount: null, maxAmount: null }));
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
  private readonly UPLOAD_POLL_INTERVAL_MS = 2000;
  /** Tope del polling de jobs: pasado este tiempo sin resultado, se corta y se avisa al usuario. */
  private readonly JOB_POLL_TIMEOUT_MS = 5 * 60_000;
  private readonly JOB_POLL_TIMEOUT_MSG =
    'El proceso está tardando más de lo normal. Por favor, reintentá en unos minutos o contactá a soporte.';

  /** Encola la subida (procesamiento en un job de Hangfire) y arranca el polling del resultado. */
  uploadFiles(
    event: UploadEvent,
    onSuccess?: () => void,
  ): void {
    this._isLoading.set(true);
    const companyId = event.companyId ?? this.companyService.activeCompany()?.id;

    this.txService.uploadFiles(
      event.files, event.bankCode, companyId, event.withoutDateFilter,
      event.forceReapplyRules, event.bankAccountId,
    ).subscribe({
      next: ({ uploadId }) => this._pollUploadResult(uploadId, event, onSuccess),
      error: () => {
        this._isLoading.set(false);
        this.toast.error('No pudimos conectar con el servidor. Revisá tu conexión o intentá de nuevo.');
      },
    });
  }

  /** Mismo patrón de polling que la generación de asientos (ver _doGenerate más abajo). */
  private _pollUploadResult(
    uploadId: string,
    event: UploadEvent,
    onSuccess?: () => void,
  ): void {
    const maxPolls = Math.ceil(this.JOB_POLL_TIMEOUT_MS / this.UPLOAD_POLL_INTERVAL_MS);
    let polls = 0;
    let finished = false;

    timer(this.UPLOAD_POLL_INTERVAL_MS, this.UPLOAD_POLL_INTERVAL_MS).pipe(
      take(maxPolls),
      tap(() => polls++),
      switchMap(() => this.txService.getUploadResult(uploadId)),
      takeWhile(envelope => !envelope.done, true),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: (envelope) => {
        if (envelope.done) {
          finished = true;
          this._handleUploadResult(envelope, event, onSuccess);
        }
      },
      error: () => {
        this._isLoading.set(false);
        this.toast.error('No pudimos conectar con el servidor. Revisá tu conexión o intentá de nuevo.');
      },
      complete: () => {
        // El stream completa sin resultado solo si se agotó el tope de intentos
        // (en destroy del servicio polls < maxPolls, así que no hay toast espurio).
        if (!finished && polls >= maxPolls) {
          this._isLoading.set(false);
          this.toast.warning(this.JOB_POLL_TIMEOUT_MSG);
        }
      },
    });
  }

  /**
   * Reproduce las mismas ramas de UX que antes resolvía la respuesta síncrona del upload, ahora a
   * partir del resultado polleado. `statusCode` 402/403 replican el texto que antes mostraba
   * error.interceptor.ts (acá ya no aplica: la respuesta es 200 con el resultado adentro, no un
   * error HTTP real).
   */
  private _handleUploadResult(
    envelope: UploadJobResultEnvelope,
    event: UploadEvent,
    onSuccess?: () => void,
  ): void {
    if (!envelope.isSuccess) {
      this._isLoading.set(false);
      if (envelope.statusCode === 402) {
        this.toast.warning('Límite del plan alcanzado. Actualizá tu suscripción en la sección Plan.');
      } else if (envelope.statusCode === 403) {
        this.toast.error('No tenés permisos para realizar esta acción.');
      } else {
        this.toast.error(envelope.error ?? 'Error al procesar el extracto. Intentá de nuevo.');
      }
      return;
    }

    const response = envelope.value!;
    const generated = response.totalProcessed > 0;
    const reapplied = (response.reappliedToExisting ?? 0) > 0;

    // Va antes que cualquier otra rama y fuera de los `if` de éxito: la cuenta se creó igual, y es
    // el aviso más importante de la carga. Sin contrapartida esos movimientos no van a poder
    // asentarse, y el usuario se enteraría recién al recibir un 422 al generar asientos.
    this._reportCreatedBankAccounts(response);

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
      if (response.parseErrors?.length) {
        this._reportParseErrors(response.parseErrors);
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
  }

  /**
   * Aviso persistente por cada cuenta bancaria que el OCR dio de alta sola (Flujo 3). Nacen sin
   * contrapartida contable, así que sus movimientos entran pero NO pueden generar asiento.
   *
   * El toast no se cierra solo a propósito: la acción que pide está en otra pantalla (la ficha de
   * la empresa), y uno de cuatro segundos se pierde justo cuando el usuario está mirando la grilla
   * que se acaba de llenar.
   */
  private _reportCreatedBankAccounts(response: UploadResponse): void {
    const created = response.createdBankAccounts ?? [];
    if (created.length === 0) return;

    const names = created.map(a => a.alias).join(', ');
    this.toast.persistent(
      created.length === 1
        ? `Se detectó una cuenta bancaria nueva (${names}). Andá a la ficha de la empresa, pestaña ` +
          'Cuentas Bancarias, y configurale su contrapartida contable: hasta entonces sus ' +
          'movimientos no van a poder asentarse.'
        : `Se detectaron ${created.length} cuentas bancarias nuevas (${names}). Andá a la ficha de ` +
          'la empresa, pestaña Cuentas Bancarias, y configurales su contrapartida contable: hasta ' +
          'entonces sus movimientos no van a poder asentarse.',
    );

    // El selector de la Dropzone tiene que ofrecerlas desde la próxima carga.
    this.bankAccountService.refresh(this.companyService.activeCompany()?.id);
  }

  /**
   * Los archivos rechazados llegan como texto libre del backend. Los tres motivos accionables
   * merecen su propio mensaje: decirle "OCR fallido" a alguien que subió un resumen consolidado lo
   * manda a revisar la calidad del PDF cuando lo que tiene que hacer es elegir la cuenta.
   */
  private _reportParseErrors(errors: string[]): void {
    const matching = (rx: RegExp) => errors.filter(e => rx.test(e));

    const mixedCurrency = matching(/m[aá]s de una moneda/i);
    const consolidated  = matching(/resumen consolidado/i);
    const unreadable    = matching(/no se pudo leer el n[uú]mero de cuenta/i);
    const rest = errors.filter(e =>
      !mixedCurrency.includes(e) && !consolidated.includes(e) && !unreadable.includes(e));

    if (mixedCurrency.length) {
      const n = mixedCurrency.length;
      this.toast.error(
        `${n} extracto${n > 1 ? 's contienen' : ' contiene'} cuentas en más de una moneda (pesos y dólares). ` +
        `Subí el extracto de cada cuenta por separado.`
      );
    }

    if (consolidated.length) {
      const n = consolidated.length;
      this.toast.error(
        `${n} extracto${n > 1 ? 's son resúmenes consolidados' : ' es un resumen consolidado'}: ` +
        `identifica${n > 1 ? 'n' : ''} más de una cuenta bancaria. Elegí la cuenta en el selector ` +
        `de la pantalla de carga y volvé a subirlo${n > 1 ? 's' : ''}.`
      );
    }

    if (unreadable.length) {
      const n = unreadable.length;
      this.toast.warning(
        `No se pudo leer el número de cuenta de ${n} extracto${n > 1 ? 's' : ''}. ` +
        `Elegí la cuenta bancaria en la pantalla de carga y volvé a subirlo${n > 1 ? 's' : ''}.`
      );
    }

    if (rest.length) {
      const n = rest.length;
      this.toast.warning(
        `${n} archivo${n > 1 ? 's' : ''} no ${n > 1 ? 'pudieron' : 'pudo'} procesarse ` +
        `(OCR fallido o formato no soportado) y ${n > 1 ? 'fueron omitidos' : 'fue omitido'}.`
      );
    }
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
        this._currencyTotals.set([]);
        this._availableAccounts.set([]);
        this._availableBankAccounts.set([]);
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
        this.toast.success(res.message || 'Generación de asientos iniciada en segundo plano. Esto puede demorar unos minutos.');

        if (res.jobId) {
          // _isGenerating queda en true durante todo el polling: el botón "Asentar" permanece
          // deshabilitado mientras el job de Hangfire corre, evitando una doble generación.
          const GENERATE_POLL_INTERVAL_MS = 3000;
          const maxPolls = Math.ceil(this.JOB_POLL_TIMEOUT_MS / GENERATE_POLL_INTERVAL_MS);
          let polls = 0;
          let finished = false;

          timer(GENERATE_POLL_INTERVAL_MS, GENERATE_POLL_INTERVAL_MS).pipe(
            take(maxPolls),
            tap(() => polls++),
            switchMap(() => this.journalEntryService.getJobStatus(res.jobId!)),
            takeWhile(status => status.state === 'Processing' || status.state === 'Enqueued', true),
            takeUntilDestroyed(this.destroyRef),
          ).subscribe({
            next: (status) => {
              if (status.state === 'Processing' || status.state === 'Enqueued') return;
              // Cualquier estado terminal (Succeeded, Failed, u otro inesperado) libera el botón.
              finished = true;
              this._isGenerating.set(false);
              if (status.state === 'Succeeded') {
                this.toast.success('¡Asientos generados correctamente!');
                this.loadData();
              } else if (status.state === 'Failed') {
                this.toast.error('No pudimos generar los asientos. Intentá de nuevo en unos minutos; si el problema sigue, contactá a soporte.');
              }
            },
            error: () => {
              this._isGenerating.set(false);
              this.toast.error('No pudimos verificar el estado de la generación de asientos. Actualizá la página en unos minutos para ver si se completó.');
            },
            complete: () => {
              if (!finished && polls >= maxPolls) {
                this._isGenerating.set(false);
                this.toast.warning(this.JOB_POLL_TIMEOUT_MSG);
              }
            },
          });
        } else {
          this._isGenerating.set(false);
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
