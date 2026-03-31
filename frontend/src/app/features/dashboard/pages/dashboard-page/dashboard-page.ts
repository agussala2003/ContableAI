import { Component, inject, signal, computed, effect } from '@angular/core';
import { DecimalPipe, NgClass } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LucideAngularModule } from 'lucide-angular';
import { CompanyService } from '../../../../core/services/company.service';
import { DashboardService, DashboardStats } from '../../dashboard.service';

interface KpiCard {
  label:       string;
  value:       number;
  description: string;
  icon:        string;
  colorClass:  string;
  iconBg:      string;
  iconColor:   string;
}

const MONTH_NAMES = [
  'Enero','Febrero','Marzo','Abril','Mayo','Junio',
  'Julio','Agosto','Septiembre','Octubre','Noviembre','Diciembre',
];

const CURRENT_YEAR = new Date().getFullYear();

@Component({
  selector: 'app-dashboard-page',
  standalone: true,
  imports: [LucideAngularModule, DecimalPipe, NgClass, FormsModule],
  templateUrl: './dashboard-page.html',
})
export class DashboardPage {
  private companyService   = inject(CompanyService);
  private dashboardService = inject(DashboardService);

  loading = signal(false);
  stats   = signal<DashboardStats | null>(null);
  error   = signal<string | null>(null);

  // ── Period filters ───────────────────────────────────────────────────────
  selectedMonth = signal<number>(new Date().getMonth() + 1);
  selectedYear  = signal<number>(CURRENT_YEAR);

  readonly monthOptions = MONTH_NAMES.map((label, i) => ({ value: i + 1, label }));
  readonly availableYears: number[] = Array.from(
    { length: CURRENT_YEAR - 2022 },
    (_, i) => CURRENT_YEAR + 1 - i   // current+1 down to 2023
  );

  // ── Derived display ──────────────────────────────────────────────────────
  protected periodLabel = computed(() =>
    `${MONTH_NAMES[this.selectedMonth() - 1]} ${this.selectedYear()}`
  );

  protected cards = computed((): KpiCard[] => {
    const s = this.stats();
    if (!s) return [];
    return [
      {
        label:       'Total del período',
        value:       s.totalTransactions,
        description: 'Movimientos importados en el mes',
        icon:        'activity',
        colorClass:  'text-indigo-600 dark:text-indigo-400',
        iconBg:      'bg-indigo-50 dark:bg-indigo-950/60',
        iconColor:   '#6366f1',
      },
      {
        label:       'Sin clasificar',
        value:       s.pendingClassification,
        description: 'Requieren cuenta contable',
        icon:        'clock',
        colorClass:  'text-amber-600 dark:text-amber-400',
        iconBg:      'bg-amber-50 dark:bg-amber-950/60',
        iconColor:   '#d97706',
      },
      {
        label:       'Clasificadas',
        value:       s.classified,
        description: 'Con cuenta contable asignada',
        icon:        'circle-check',
        colorClass:  'text-emerald-600 dark:text-emerald-400',
        iconBg:      'bg-emerald-50 dark:bg-emerald-950/60',
        iconColor:   '#059669',
      },
      {
        label:       'Baja confianza',
        value:       s.lowConfidence,
        description: 'Clasificación automática < 50%',
        icon:        'triangle-alert',
        colorClass:  'text-rose-600 dark:text-rose-400',
        iconBg:      'bg-rose-50 dark:bg-rose-950/60',
        iconColor:   '#e11d48',
      },
    ];
  });

  protected skeletonCards = Array.from({ length: 4 });

  constructor() {
    // Re-fetch whenever company, month, or year changes.
    effect(() => {
      const company = this.companyService.activeCompany();
      const month   = this.selectedMonth();
      const year    = this.selectedYear();
      if (company) {
        this.load(company.id, month, year);
      } else {
        this.stats.set(null);
        this.loading.set(false);
      }
    });
  }

  protected get companyName(): string {
    return this.companyService.activeCompany()?.name ?? '';
  }

  protected refresh(): void {
    const id = this.companyService.activeCompany()?.id;
    if (id) this.load(id, this.selectedMonth(), this.selectedYear());
  }

  private load(companyId: string, month: number, year: number): void {
    this.loading.set(true);
    this.error.set(null);
    this.dashboardService.getStats(companyId, month, year).subscribe({
      next: data => {
        this.stats.set(data);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('No se pudieron cargar los datos del dashboard.');
        this.loading.set(false);
      },
    });
  }
}
