import { Component, inject, signal, OnInit, viewChild } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CompanyService } from '../../../../core/services/company.service';
import { AuthService } from '../../../../core/services/auth.service';
import { ChartOfAccountService } from '../../../../core/services/chart-of-account.service';
import { ReconciliationService } from '../../reconciliation.service';
import { UploadZone } from '../../components/upload-zone/upload-zone';
import { TransactionGrid } from '../../components/transaction-grid/transaction-grid';
import { TransactionSkeleton } from '../../../../shared/components/transaction-skeleton';
import { UploadModal } from '../../components/upload-modal/upload-modal';
import { CompanyModal } from '../../components/company-modal/company-modal';
import { OnboardingModal } from '../../../../core/components/onboarding-modal/onboarding-modal';
import { RuleService, SaveRuleRequest } from '../../../../core/services/rule.service';
import { ToastService } from '../../../../core/services/toast.service';
import { LucideAngularModule } from 'lucide-angular';


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
    CompanyModal,
    OnboardingModal,
    LucideAngularModule,
  ],
  templateUrl: './reconciliation-page.html',
  styleUrl: './reconciliation-page.scss',
})
export class ReconciliationPage implements OnInit {

  readonly svc            = inject(ReconciliationService);
  readonly companyService = inject(CompanyService);
  readonly authService    = inject(AuthService);
  readonly chartService   = inject(ChartOfAccountService);
  readonly ruleService    = inject(RuleService);
  readonly toast          = inject(ToastService);

  // ── UI-only signals (not business state) ────────────────────────────────
  showUploadModal  = signal(false);
  showCompanyModal = signal(false);
  howItWorksOpen   = signal(false);
  showQuickRuleModal = signal(false);
  isSavingQuickRule = signal(false);
  showOnboarding   = signal<boolean>(
    localStorage.getItem('contableai_onboarding_done') !== 'true'
  );
  gridSelectedIds  = signal<string[]>([]);
  quickRuleKeyword = signal('');
  quickRuleTargetAccount = signal('');
  quickRuleDirection = signal<'DEBIT' | 'CREDIT' | null>(null);
  quickRulePriority = signal(100);
  quickRuleRequiresTax = signal(false);

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

  updateTransaction(event: { id: string; newAccount: string }): void {
    this.svc.updateTransaction(event.id, event.newAccount);
  }

  onGridSelectionChange(ids: string[]): void {
    this.gridSelectedIds.set(ids);
  }

  openQuickRuleModal(): void {
    this.quickRuleKeyword.set('');
    this.quickRuleTargetAccount.set('');
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
    this.ruleService.createRule(companyId, req).subscribe({
      next: (rule) => {
        this.ruleService.reapplyRule(rule.id).subscribe({
          next: (result) => {
            this.isSavingQuickRule.set(false);
            this.closeQuickRuleModal();
            this.svc.loadData();
            this.toast.success(`Regla creada y aplicada a ${result.updatedCount} movimiento(s) pendiente(s).`);
          },
          error: () => {
            this.isSavingQuickRule.set(false);
            this.closeQuickRuleModal();
            this.svc.loadData();
            this.toast.warning('Regla creada, pero no se pudo completar la reaplicación automática.');
          },
        });
      },
      error: () => {
        this.isSavingQuickRule.set(false);
        this.toast.error('No se pudo crear la regla rápida.');
      },
    });
  }

  /** Selects only the eligible (unbooked) transactions in the grid. */
  selectForGenerate(): void {
    this.txGrid()?.setSelection(this.svc.eligibleIds());
  }

  generateEntries(): void {
    this.svc.generateEntries(this.gridSelectedIds());
  }

  onFileDropped(event: { files: File[]; bankCode: string; companyId?: string }): void {
    this.svc.uploadFiles(event, () => this.showUploadModal.set(false));
  }

  onOnboardingStart(): void {
    this.showOnboarding.set(false);
    this.showCompanyModal.set(true);
  }

  onOnboardingSkip(): void {
    this.showOnboarding.set(false);
  }

  logout(): void {
    this.authService.logout();
  }
}

