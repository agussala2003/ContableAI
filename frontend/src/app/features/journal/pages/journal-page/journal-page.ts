import { ChangeDetectionStrategy, Component, inject, signal, computed, effect } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { JournalEntryService, JournalEntry, JournalEntryLine } from '../../../../core/services/journal-entry.service';
import { CompanyService } from '../../../../core/services/company.service';
import { ToastService } from '../../../../core/services/toast.service';
import { ConfirmDialogService } from '../../../../core/services/confirm-dialog.service';
import { ConfigService } from '../../../../core/config/config.service';
import { CompanyModal } from '../../../reconciliation/components/company-modal/company-modal';
import { TransactionSkeleton } from '../../../../shared/components/transaction-skeleton';
import { LucideAngularModule } from 'lucide-angular';

export interface AccountGroup {
  key: string;
  account: string;
  debit: number;
  credit: number;
  lines: Array<{ entry: JournalEntry; line: JournalEntryLine }>;
}

@Component({
  selector: 'app-journal-page',
  standalone: true,
  imports: [DecimalPipe, FormsModule, LucideAngularModule, CompanyModal, TransactionSkeleton],
  templateUrl: './journal-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class JournalPage {
  // ── Icons ───────────────────────────────────────────────────

  // ── Services ────────────────────────────────────────────────
  private journalService = inject(JournalEntryService);
  companyService         = inject(CompanyService);
  private configService  = inject(ConfigService);
  private toast          = inject(ToastService);
  private confirmDialog  = inject(ConfirmDialogService);

  // ── UI state ────────────────────────────────────────────────
  showCompanyModal = signal(false);
  viewMode         = signal<'journal' | 'ledger'>('ledger');
  hasRenderedJournal = signal(false);
  hasRenderedLedger  = signal(true);
  isSwitchingView  = signal(false);
  expandedAccounts = signal<Set<string>>(new Set());

  // ── Data ────────────────────────────────────────────────────
  entries             = signal<JournalEntry[]>([]);
  isLoading           = signal(false);
  isExporting         = signal(false);
  isExportingHolistor = signal(false);
  isExportingBejerman = signal(false);
  isDeletingAll       = signal(false);
  deletingId          = signal<string | null>(null);
  private loadSeq = 0;

  // ── Filters ─────────────────────────────────────────────────
  selectedMonth     = signal<number | null>(null);
  selectedYear      = signal<number | null>(null);
  searchDescription = signal('');
  searchAccount     = signal('');

  readonly months = [
    { value: 1,  label: 'Enero' },     { value: 2,  label: 'Febrero' },
    { value: 3,  label: 'Marzo' },     { value: 4,  label: 'Abril' },
    { value: 5,  label: 'Mayo' },      { value: 6,  label: 'Junio' },
    { value: 7,  label: 'Julio' },     { value: 8,  label: 'Agosto' },
    { value: 9,  label: 'Septiembre'}, { value: 10, label: 'Octubre' },
    { value: 11, label: 'Noviembre' }, { value: 12, label: 'Diciembre' },
  ];

  // ── Computed ────────────────────────────────────────────────

  /** Entries filtered by description and account (client-side). */
  filteredEntries = computed(() => {
    const desc = this.searchDescription().toLowerCase().trim();
    const acc  = this.searchAccount();
    const month = this.selectedMonth();
    const year = this.selectedYear();

    return this.entries().filter(e => {
      const entryYear = Number(e.date.slice(0, 4));
      const entryMonth = Number(e.date.slice(5, 7));

      if (year && entryYear !== year) return false;
      if (month && entryMonth !== month) return false;
      if (desc && !e.description.toLowerCase().includes(desc)) return false;
      if (acc  && !e.lines.some(l => l.account === acc))       return false;
      return true;
    });
  });

  /** Unique account names present in loaded entries (for the filter dropdown). */
  uniqueAccounts = computed(() => {
    const set = new Set<string>();
    this.entries().forEach(e => e.lines.forEach(l => set.add(l.account)));
    return Array.from(set).sort((a, b) => a.localeCompare(b));
  });

  /** Unique years present in loaded entries (for dynamic filter options). */
  availableYears = computed(() => {
    const years = new Set<number>();
    for (const e of this.entries()) {
      years.add(Number(e.date.slice(0, 4)));
    }
    return Array.from(years).sort((a, b) => b - a);
  });

  /** Unique months present in loaded entries; when year is selected, months are constrained to that year. */
  availableMonths = computed(() => {
    const selectedYear = this.selectedYear();
    const months = new Set<number>();
    for (const e of this.entries()) {
      const year = Number(e.date.slice(0, 4));
      if (selectedYear && year !== selectedYear) continue;
      months.add(Number(e.date.slice(5, 7)));
    }
    return Array.from(months).sort((a, b) => a - b);
  });

  /** Groups all lines by account for the Ledger (Mayor) view. */
  accountGroups = computed((): AccountGroup[] => {
    const map = new Map<string, AccountGroup>();
    const accFilter = this.searchAccount();
    const balanceAccount = this.companyService.activeCompany()?.bankAccountName?.trim().toLowerCase() ?? null;

    const groupKeyFor = (line: JournalEntryLine): string => {
      const baseAccount = line.account.trim();
      const isBalanceAccount = !!balanceAccount && baseAccount.toLowerCase() === balanceAccount;
      if (!isBalanceAccount) return baseAccount;
      return line.isDebit ? `${baseAccount}__debit` : `${baseAccount}__credit`;
    };

    const groupLabelFor = (line: JournalEntryLine): string => {
      const baseAccount = line.account.trim();
      const isBalanceAccount = !!balanceAccount && baseAccount.toLowerCase() === balanceAccount;
      if (!isBalanceAccount) return baseAccount;
      return line.isDebit ? `${baseAccount} (Debe)` : `${baseAccount} (Haber)`;
    };

    for (const entry of this.filteredEntries()) {
      for (const line of entry.lines) {
        if (accFilter && line.account !== accFilter) continue;

        const key = groupKeyFor(line);
        if (!map.has(key)) {
          map.set(key, { key, account: groupLabelFor(line), debit: 0, credit: 0, lines: [] });
        }

        const grp = map.get(key)!;
        if (line.isDebit) grp.debit  += line.amount;
        else              grp.credit += line.amount;
        grp.lines.push({ entry, line });
      }
    }

    return Array.from(map.values()).sort((a, b) => {
      const baseA = a.account.replace(/ \((Debe|Haber)\)$/i, '');
      const baseB = b.account.replace(/ \((Debe|Haber)\)$/i, '');
      const baseCompare = baseA.localeCompare(baseB);
      if (baseCompare !== 0) return baseCompare;
      if (a.account.endsWith('(Debe)')) return -1;
      if (b.account.endsWith('(Debe)')) return 1;
      return a.account.localeCompare(b.account);
    });
  });

  totalDebit = computed(() =>
    this.accountGroups().reduce((sum, g) => sum + g.debit, 0)
  );

  totalCredit = computed(() =>
    this.accountGroups().reduce((sum, g) => sum + g.credit, 0)
  );

  totalLines = computed(() =>
    this.accountGroups().reduce((sum, g) => sum + g.lines.length, 0)
  );

  isBalanced = computed(() =>
    Math.abs(this.totalDebit() - this.totalCredit()) < 0.01
  );

  periodLabel = computed(() => {
    const m = this.selectedMonth();
    const y = this.selectedYear();
    if (m && y) return `${this.months.find(mo => mo.value === m)?.label ?? m} ${y}`;
    if (y) return `${y}`;
    return 'Todos los períodos';
  });

  hasActiveFilters = computed(() =>
    this.searchDescription().trim() !== '' ||
    this.searchAccount()              !== '' ||
    this.selectedMonth()              !== null ||
    this.selectedYear()               !== null
  );

  constructor() {
    effect(() => {
      const company = this.companyService.activeCompany();
      if (!company?.id) {
        this.entries.set([]);
        this.isLoading.set(false);
        return;
      }
      this.load(company.id);
    });
  }

  // ── Methods ─────────────────────────────────────────────────

  onCompanySelectChange(id: string): void {
    const company = this.companyService.companies().find(c => c.id === id);
    if (company) this.companyService.selectCompany(company);
  }

  clearFilters(): void {
    this.searchDescription.set('');
    this.searchAccount.set('');
    this.selectedMonth.set(null);
    this.selectedYear.set(null);
  }

  setViewMode(mode: 'journal' | 'ledger'): void {
    if (this.viewMode() === mode) return;

    this.isSwitchingView.set(true);

    requestAnimationFrame(() => {
      this.viewMode.set(mode);
      if (mode === 'journal') this.hasRenderedJournal.set(true);
      if (mode === 'ledger') this.hasRenderedLedger.set(true);

      requestAnimationFrame(() => this.isSwitchingView.set(false));
    });
  }

  toggleAccount(account: string): void {
    this.expandedAccounts.update(set => {
      const next = new Set(set);
      if (next.has(account)) next.delete(account);
      else next.add(account);
      return next;
    });
  }

  isExpanded(account: string): boolean {
    return this.expandedAccounts().has(account);
  }

  load(companyId?: string): void {
    if (!companyId) {
      this.entries.set([]);
      this.isLoading.set(false);
      return;
    }

    const requestSeq = ++this.loadSeq;
    this.isLoading.set(true);
    this.journalService.getEntries({ companyId }).subscribe({
      next: list => {
        if (requestSeq !== this.loadSeq) return;
        this.entries.set(list);
        this.isLoading.set(false);
      },
      error: () => {
        if (requestSeq !== this.loadSeq) return;
        this.toast.error('Error al cargar los asientos.');
        this.isLoading.set(false);
      },
    });
  }

  async deleteEntry(id: string): Promise<void> {
    const ok = await this.confirmDialog.confirm({
      title: '¿Eliminar este asiento?',
      message: 'La transacción quedará sin asentar.',
    });
    if (!ok) return;
    this.deletingId.set(id);
    this.journalService.deleteEntry(id).subscribe({
      next: () => {
        this.entries.update(list => list.filter(e => e.id !== id));
        this.toast.success('Asiento eliminado.');
        this.deletingId.set(null);
      },
      error: () => {
        this.toast.error('Error al eliminar el asiento.');
        this.deletingId.set(null);
      },
    });
  }

  async deleteAllEntries(): Promise<void> {
    const company = this.companyService.activeCompany();
    if (!company?.id) {
      this.toast.error('Seleccioná una empresa para borrar asientos.');
      return;
    }

    const month = this.selectedMonth();
    const year = this.selectedYear();

    if (month && !year) {
      this.toast.warning('Para borrar por mes, primero seleccioná también el año.');
      return;
    }

    const periodLabel = month && year ? `${String(month).padStart(2, '0')}/${year}` : year ? `${year}` : 'todos los períodos';

    const ok = await this.confirmDialog.confirm({
      title: '¿Borrar todos los asientos? ',
      message: `Se eliminarán todos los asientos de ${company.name} (${periodLabel}) y los movimientos quedarán sin asentar.`,
      confirmLabel: 'Sí, borrar todos',
    });
    if (!ok) return;

    this.isDeletingAll.set(true);
    this.journalService.deleteAll({
      companyId: company.id,
      month: month ?? undefined,
      year: year ?? undefined,
    }).subscribe({
      next: (res) => {
        this.isDeletingAll.set(false);
        this.load(company.id);

        if (res.deletedEntries > 0) {
          this.toast.success(
            `Se eliminaron ${res.deletedEntries} asiento${res.deletedEntries !== 1 ? 's' : ''} (${res.scopeDescription}).`
          );
        } else {
          this.toast.warning('No había asientos para borrar en el alcance seleccionado.');
        }
      },
      error: (err) => {
        this.isDeletingAll.set(false);
        const detail: string = err?.error?.message ?? err?.error?.detail ?? err?.error?.title ?? '';
        this.toast.error(detail || 'No se pudieron borrar los asientos.');
      },
    });
  }

  exportExcel(): void {
    this.isExporting.set(true);
    const company = this.companyService.activeCompany();
    const entryIds = this.filteredEntries().map(e => e.id);
    this.journalService.downloadExcel(
      company?.id,
      this.selectedMonth() ?? undefined,
      this.selectedYear() ?? undefined,
      this.searchDescription().trim() || undefined,
      this.searchAccount() || undefined,
      entryIds,
    );
    setTimeout(() => this.isExporting.set(false), this.configService.config().exportCooldownMs);
  }

  exportHolistor(): void {
    this.isExportingHolistor.set(true);
    const company = this.companyService.activeCompany();
    this.journalService.downloadHolistor(company?.id, this.selectedMonth() ?? undefined, this.selectedYear() ?? undefined);
    setTimeout(() => this.isExportingHolistor.set(false), this.configService.config().exportCooldownMs);
  }

  exportBejerman(): void {
    this.isExportingBejerman.set(true);
    const company = this.companyService.activeCompany();
    this.journalService.downloadBejerman(company?.id, this.selectedMonth() ?? undefined, this.selectedYear() ?? undefined);
    setTimeout(() => this.isExportingBejerman.set(false), this.configService.config().exportCooldownMs);
  }

  formatDate(dateStr: string): string {
    const [year, month, day] = dateStr.split('-');
    return `${day}/${month}/${year}`;
  }

  monthLabel(month: number): string {
    return this.months.find(m => m.value === month)?.label ?? String(month);
  }
}
