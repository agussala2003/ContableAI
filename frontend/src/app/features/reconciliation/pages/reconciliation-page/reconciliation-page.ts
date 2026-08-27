import { Component, inject, signal, computed, effect, OnInit, viewChild, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { timer, switchMap, take } from 'rxjs';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CompanyService } from '../../../../core/services/company.service';
import { STORAGE_KEYS } from '../../../../core/utils/storage-keys';
import { AuthService } from '../../../../core/services/auth.service';
import { ChartOfAccountService } from '../../../../core/services/chart-of-account.service';
import { ReconciliationService, UploadEvent } from '../../reconciliation.service';
import { UploadZone } from '../../components/upload-zone/upload-zone';
import { TransactionGrid } from '../../components/transaction-grid/transaction-grid';
import { TransactionSkeleton } from '../../../../shared/components/transaction-skeleton';
import { UploadModal } from '../../components/upload-modal/upload-modal';
import { AfipZone } from '../../components/afip-zone/afip-zone';
import { CompanyModal } from '../../components/company-modal/company-modal';
import { DuplicatesModal } from '../../components/duplicates-modal/duplicates-modal';
import { OnboardingModal } from '../../../../core/components/onboarding-modal/onboarding-modal';
import { ReapplyRuleModal } from '../../../rules/components/reapply-rule-modal/reapply-rule-modal';
import { RuleService, SaveRuleRequest, AccountingRule } from '../../../../core/services/rule.service';
import { SkippedDuplicate } from '../../../../core/services/transaction';
import { CurrencySymbolPipe } from '../../../../shared/pipes/currency-amount.pipe';
import { KeyboardService } from '../../../../core/services/keyboard.service';
import { AfipService } from '../../afip.service';
import { ToastService } from '../../../../core/services/toast.service';
import { TourService } from '../../../../core/services/tour.service';
import { LucideAngularModule } from 'lucide-angular';
import { ModalA11yDirective } from '../../../../shared/directives/modal-a11y.directive';

const DEMO_CSV = `Fecha,Referencia,Descripcion,Numero,Importe
01/05/2026,,MERCADO PAGO COBRANZA DIGITAL,,125000.00
02/05/2026,,TRANSFERENCIA RECIBIDA CLIENTE ABC SRL,,75000.00
03/05/2026,,HONORARIOS ESTUDIO CONTABLE,,-45000.00
05/05/2026,,AFIP VEP 0031 IMPUESTO A LAS GANANCIAS,,-32000.00
06/05/2026,,COBRANZA CLIENTE XYZ INGENIERIA,,200000.00
07/05/2026,,DEBITO AUTOMATICO EXPENSAS EDIFICIO,,-8500.00
08/05/2026,,TRANSFERENCIA RECIBIDA CLIENTE DEF SA,,95000.00
09/05/2026,,IMPUESTO INGRESOS BRUTOS MAYO,,-18000.00
12/05/2026,,SERVICIOS CLARO TELEFONIA,,-5500.00
15/05/2026,,SUELDO EMPLEADO GONZALEZ MARIA,,-98000.00
16/05/2026,,MERCADO PAGO COBRANZA DIGITAL,,89000.00
18/05/2026,,TRANSFERENCIA ENVIADA PROVEEDOR SUMINISTROS,,-22000.00
20/05/2026,,COBRANZA CLIENTE ABC SRL,,55000.00
22/05/2026,,AFIP VEP SEGURIDAD SOCIAL,,-15000.00
28/05/2026,,MERCADO PAGO COBRANZA DIGITAL,,112000.00
`;



@Component({
  selector: 'app-reconciliation-page',
  standalone: true,
  providers: [ReconciliationService],
  imports: [
    DecimalPipe,
    FormsModule,
    UploadZone,
    TransactionGrid,
    TransactionSkeleton,
    UploadModal,
    AfipZone,
    CompanyModal,
    DuplicatesModal,
    OnboardingModal,
    ReapplyRuleModal,
    LucideAngularModule,
    CurrencySymbolPipe,
    ModalA11yDirective,
  ],
  templateUrl: './reconciliation-page.html',
})
export class ReconciliationPage implements OnInit {

  readonly svc             = inject(ReconciliationService);
  readonly companyService  = inject(CompanyService);
  readonly authService     = inject(AuthService);
  readonly chartService    = inject(ChartOfAccountService);
  readonly ruleService     = inject(RuleService);
  readonly afipService     = inject(AfipService);
  readonly toast           = inject(ToastService);
  readonly tour            = inject(TourService);
  readonly keyboardService = inject(KeyboardService);
  private readonly destroyRef = inject(DestroyRef);

  // ── UI-only signals (not business state) ────────────────────────────────
  isAfipRematching    = signal(false);
  showUploadModal     = signal(false);
  showCompanyModal    = signal(false);
  pendingDuplicates   = signal<SkippedDuplicate[]>([]);
  showDuplicatesModal = computed(() => this.pendingDuplicates().length > 0);
  howItWorksOpen   = signal(false);
  showQuickRuleModal = signal(false);
  showBulkRuleModal = signal(false);
  isSavingQuickRule = signal(false);
  /** Regla recién creada por el modal rápido, a la espera de confirmar la aplicación retroactiva. */
  reapplyingRule = signal<AccountingRule | null>(null);
  isApplyingBulkRule = signal(false);
  showOnboarding   = signal<boolean>(
    localStorage.getItem(STORAGE_KEYS.onboardingDone) !== 'true'
  );
  gridSelectedIds  = signal<string[]>([]);
  quickRuleKeyword = signal('');
  quickRuleTargetAccount = signal('');
  quickRuleDirection = signal<'DEBIT' | 'CREDIT' | null>(null);
  quickRulePriority = signal(100);
  quickRuleRequiresTax = signal(false);
  bulkRuleIds = signal<string[]>([]);
  selectedBulkRuleId = signal('');
  bulkRuleSearch = signal('');
  bulkRules = signal<AccountingRule[]>([]);

  filteredBulkRules = computed(() => {
    const query = this.bulkRuleSearch().trim().toLowerCase();
    const rules = this.bulkRules();
    if (!query) {
      return [...rules].sort((a, b) => a.priority - b.priority || a.keyword.localeCompare(b.keyword));
    }

    return rules
      .filter(r =>
        r.keyword.toLowerCase().includes(query)
        || r.targetAccount.toLowerCase().includes(query)
      )
      .sort((a, b) => a.priority - b.priority || a.keyword.localeCompare(b.keyword));
  });

  private txGrid = viewChild(TransactionGrid);

  // ── Static display data ──────────────────────────────────────────────────
  readonly months      = [
    { value: 1,  label: 'Enero' },      { value: 2,  label: 'Febrero' },
    { value: 3,  label: 'Marzo' },      { value: 4,  label: 'Abril' },
    { value: 5,  label: 'Mayo' },       { value: 6,  label: 'Junio' },
    { value: 7,  label: 'Julio' },      { value: 8,  label: 'Agosto' },
    { value: 9,  label: 'Septiembre' }, { value: 10, label: 'Octubre' },
    { value: 11, label: 'Noviembre' },  { value: 12, label: 'Diciembre' },
  ];
  readonly pageSizeOptions = [10, 50, 100] as const;

  constructor() {
    effect(() => {
      const tick = this.ruleService.transactionRefreshTick();
      if (tick > 0) this.svc.loadData();
    });
    effect(() => {
      const dups = this.svc.skippedDuplicates();
      if (dups.length) this.pendingDuplicates.set(dups);
    });
  }

  ngOnInit(): void {
    this.svc.init();
  }

  // ── Event handlers ───────────────────────────────────────────────────────

  onCompanySelectChange(id: string): void {
    const company = this.companyService.companies().find(c => c.id === id);
    if (company) this.companyService.selectCompany(company);
  }

  onSortChange(event: { column: string | null; dir: 'asc' | 'desc' | null }): void {
    this.svc.applySort(event.column, event.dir);
  }

  onPageSizeChange(value: string): void {
    if (value === 'all') {
      this.svc.setPageSize(500);
      return;
    }

    const parsed = Number(value);
    if (Number.isFinite(parsed) && parsed > 0) {
      this.svc.setPageSize(parsed);
    }
  }

  onSearchChange(value: string): void {
    this.svc.setFilter({ search: value });

    // Prevent stale empty table: when search is cleared, reload unfiltered data immediately.
    if (!value?.trim()) {
      this.svc.applyFilters();
    }
  }

  accountLabel(account: string): string {
    return account === 'Pending' ? '— Pendiente —' : account;
  }

  onBulkAssigned(event: { ids: string[]; account: string }): void {
    this.svc.onBulkAssigned(event.ids, event.account);
  }

  openBulkRuleModal(ids: string[]): void {
    if (ids.length === 0) {
      this.toast.warning('Seleccioná al menos un movimiento.');
      return;
    }

    const companyId = this.companyService.activeCompany()?.id;
    if (!companyId) {
      this.toast.error('No hay empresa activa.');
      return;
    }

    this.bulkRuleIds.set(ids);
    this.selectedBulkRuleId.set('');
    this.bulkRuleSearch.set('');
    this.showBulkRuleModal.set(true);

    this.ruleService.getRules(companyId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (rules) => this.bulkRules.set(rules),
      error: () => {
        this.bulkRules.set([]);
        this.toast.error('No se pudieron cargar las reglas.');
      },
    });
  }

  closeBulkRuleModal(): void {
    this.showBulkRuleModal.set(false);
    this.selectedBulkRuleId.set('');
    this.bulkRuleSearch.set('');
    this.bulkRuleIds.set([]);
  }

  openQuickRuleFromBulk(): void {
    this.showBulkRuleModal.set(false);
    this.openQuickRuleModal();
  }

  applySelectedRuleBulk(): void {
    const ruleId = this.selectedBulkRuleId();
    if (!ruleId) {
      this.toast.warning('Seleccioná una regla para aplicar.');
      return;
    }

    const selectedRule = this.bulkRules().find(r => r.id === ruleId);
    if (!selectedRule) {
      this.toast.error('La regla seleccionada ya no está disponible.');
      return;
    }

    this.isApplyingBulkRule.set(true);
    this.svc.onBulkRuleApplied(this.bulkRuleIds(), {
      id: selectedRule.id,
      keyword: selectedRule.keyword,
      targetAccount: selectedRule.targetAccount,
    });
    this.isApplyingBulkRule.set(false);
    this.closeBulkRuleModal();
  }

  updateTransaction(event: { id: string; newAccount: string }): void {
    this.svc.updateTransaction(event.id, event.newAccount);
  }

  onGridSelectionChange(ids: string[]): void {
    this.gridSelectedIds.set(ids);
  }

  openQuickRuleModal(): void {
    // Reset fields for a generic empty rule creation
    this.quickRuleKeyword.set('');
    this.quickRuleTargetAccount.set('');
    this.quickRuleDirection.set(null);
    this.quickRulePriority.set(100);
    this.quickRuleRequiresTax.set(false);
    this.showQuickRuleModal.set(true);
  }

  /** Handles payload from grid to pre-fill rule creation */
  handleCreateRule(event: { description: string; account?: string }): void {
    this.quickRuleKeyword.set(event.description ?? '');
    this.quickRuleTargetAccount.set(event.account ?? '');
    this.quickRuleDirection.set(null);
    this.quickRulePriority.set(100);
    this.quickRuleRequiresTax.set(false);
    this.showQuickRuleModal.set(true);
  }

  closeQuickRuleModal(): void {
    this.showQuickRuleModal.set(false);
    this.quickRuleKeyword.set('');
    this.quickRuleTargetAccount.set('');
    this.quickRuleDirection.set(null);
    this.quickRulePriority.set(100);
    this.quickRuleRequiresTax.set(false);
  }

  saveQuickRule(): void {
    const companyId = this.companyService.activeCompany()?.id;
    if (!companyId) {
      this.toast.error('No hay empresa activa.');
      return;
    }

    const keyword = this.quickRuleKeyword().trim();
    const targetAccount = this.quickRuleTargetAccount().trim();
    if (!keyword || !targetAccount) {
      this.toast.warning('Keyword y cuenta contable son obligatorios.');
      return;
    }

    const req: SaveRuleRequest = {
      keyword,
      targetAccount,
      direction: this.quickRuleDirection(),
      priority: this.quickRulePriority(),
      requiresTaxMatching: this.quickRuleRequiresTax(),
    };

    this.isSavingQuickRule.set(true);
    this.ruleService.createRule(companyId, req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (rule) => {
        this.bulkRules.update(list => [rule, ...list]);
        this.isSavingQuickRule.set(false);
        this.closeQuickRuleModal();
        this.toast.success('Regla creada.');
        // Encadena el flujo retroactivo: el usuario acaba de crear la regla mirando un movimiento
        // concreto, así que lo natural es ofrecerle aplicarla a lo ya cargado. Va con preview y
        // confirmación porque sobrescribe asignaciones manuales.
        this.reapplyingRule.set(rule);
      },
      error: () => {
        this.isSavingQuickRule.set(false);
        this.toast.error('No se pudo crear la regla rápida.');
      },
    });
  }

  onReapplyClosed(): void {
    this.reapplyingRule.set(null);
    // Se recarga igual si el usuario canceló: la regla nueva ya existe y la grilla la refleja.
    this.svc.loadData();
  }

  /** Selects only the eligible (unbooked) transactions in the grid. */
  selectForGenerate(): void {
    this.txGrid()?.setSelection(this.svc.eligibleIds());
  }

  generateEntries(): void {
    this.svc.generateEntries(this.gridSelectedIds());
  }

  onFileDropped(event: UploadEvent): void {
    this.svc.uploadFiles(event, () => this.showUploadModal.set(false));
  }

  onOnboardingStart(): void {
    this.showOnboarding.set(false);
    this.showCompanyModal.set(true);
  }

  onOnboardingSkip(): void {
    this.showOnboarding.set(false);
  }

  onOnboardingLoadDemo(): void {
    this.showOnboarding.set(false);
    const company = this.companyService.activeCompany();
    if (!company) {
      this.toast.info('Primero creá una empresa para cargar los datos de prueba.');
      this.showCompanyModal.set(true);
      return;
    }
    this.loadDemoData();
  }

  loadDemoData(): void {
    const companyId = this.companyService.activeCompany()?.id;
    if (!companyId) {
      this.toast.warning('Seleccioná una empresa antes de cargar los datos de prueba.');
      return;
    }

    const blob = new Blob([DEMO_CSV], { type: 'text/csv' });
    const file = new File([blob], 'demo-extracto-mayo-2026.csv', { type: 'text/csv' });

    this.toast.info('Cargando datos de prueba…');
    this.svc.uploadFiles(
      { files: [file], bankCode: 'GALICIA', companyId, withoutDateFilter: true },
      () => {
        if (!this.tour.wasDone) {
          setTimeout(() => this.tour.start(), 600);
        }
      },
    );
  }

  triggerAfipRematch(): void {
    const companyId = this.companyService.activeCompany()?.id;
    if (!companyId) return;

    this.isAfipRematching.set(true);
    const initialPending = this.svc.pendingAfipCount();

    this.afipService.triggerRematch(companyId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.toast.info('Cruce AFIP iniciado en segundo plano…');
        this.pollAfipResult(companyId, initialPending);
      },
      error: () => {
        this.toast.error('No se pudo iniciar el cruce AFIP.');
        this.isAfipRematching.set(false);
      },
    });
  }

  private pollAfipResult(companyId: string, initialPending: number): void {
    let found = false;
    timer(2000, 2000).pipe(
      take(10),
      switchMap(() => this.afipService.getVouchers(companyId)),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: (vouchers) => {
        if (found) return;
        const newPending = vouchers.filter(v => !v.isMatched).length;
        const matched    = initialPending - newPending;
        if (matched > 0) {
          found = true;
          this.svc.loadData();
          this.toast.success(`Cruce completo: ${matched} comprobante${matched > 1 ? 's' : ''} cruzado${matched > 1 ? 's' : ''} exitosamente.`);
          this.isAfipRematching.set(false);
        }
      },
      error: () => { this.isAfipRematching.set(false); },
      complete: () => {
        if (!found) {
          this.svc.refreshAfipCount();
          this.toast.info('Cruce finalizado. No se encontraron nuevos movimientos para cruzar.');
          this.isAfipRematching.set(false);
        }
      },
    });
  }

  onAfipSkippedDuplicates(dups: SkippedDuplicate[]): void {
    this.pendingDuplicates.set(dups);
  }

  closeDuplicatesModal(): void {
    this.pendingDuplicates.set([]);
    this.svc.clearSkippedDuplicates();
  }

  logout(): void {
    this.authService.logout();
  }
}

