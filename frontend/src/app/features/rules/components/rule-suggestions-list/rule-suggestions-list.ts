import { Component, effect, inject, output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { LucideAngularModule } from 'lucide-angular';
import { RuleService, RuleSuggestion } from '../../../../core/services/rule.service';
import { CompanyService } from '../../../../core/services/company.service';
import { ToastService } from '../../../../core/services/toast.service';

@Component({
  selector: 'app-rule-suggestions-list',
  standalone: true,
  imports: [CommonModule, LucideAngularModule],
  template: `
    <div class="mb-6 bg-gradient-to-r from-indigo-50 to-violet-50 dark:from-indigo-950/30 dark:to-violet-950/30 border border-indigo-100 dark:border-indigo-800/50 rounded-xl p-4 md:p-5">
      <div class="flex items-center gap-2 mb-3">
        <lucide-icon name="sparkles" class="w-5 h-5 text-indigo-600 dark:text-indigo-400"></lucide-icon>
        <h2 class="text-sm font-semibold text-indigo-950 dark:text-indigo-100">Aprendizaje Proactivo</h2>
        @if (suggestions().length > 0) {
          <span class="bg-indigo-100 dark:bg-indigo-900 text-indigo-700 dark:text-indigo-300 text-[10px] font-bold px-2 py-0.5 rounded-full">{{ suggestions().length }} sugerencias</span>
        }
        <button (click)="recalculate()" [disabled]="isRecalculating()"
                title="Analizar clasificaciones manuales y generar nuevas sugerencias"
                class="ml-auto inline-flex items-center gap-1 px-2 py-1 rounded-lg text-[11px] font-semibold
                       text-indigo-600 dark:text-indigo-400 border border-indigo-200 dark:border-indigo-500/30
                       bg-white dark:bg-slate-900 hover:bg-indigo-50 dark:hover:bg-indigo-500/10
                       disabled:opacity-50 disabled:cursor-not-allowed transition-colors">
          @if (isRecalculating()) {
            <lucide-icon name="loader-2" class="w-3 h-3 animate-spin"></lucide-icon>
          } @else {
            <lucide-icon name="refresh-cw" class="w-3 h-3"></lucide-icon>
          }
          Recalcular
        </button>
      </div>

      @if (suggestions().length > 0) {
        <p class="text-xs text-indigo-700 dark:text-indigo-300 mb-4 max-w-2xl">
          PreSal detectó patrones en tus clasificaciones manuales. Aceptar estas sugerencias creará reglas automáticas para futuras transacciones.
        </p>

        <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
          @for (sug of suggestions(); track sug.id) {
            <div class="bg-white dark:bg-slate-900 border border-indigo-100 dark:border-indigo-800/50 rounded-lg p-3 shadow-sm flex flex-col gap-3 group relative overflow-hidden">
              <div class="flex justify-between items-start">
                <div class="min-w-0 flex-1">
                  <div class="flex items-center gap-1.5 mb-1">
                    <span class="text-xs font-medium text-slate-800 dark:text-slate-200 truncate">{{ sug.keyword }}</span>
                    <span class="shrink-0 text-[10px] bg-slate-100 dark:bg-slate-800 text-slate-500 px-1.5 py-0.5 rounded font-mono">{{ sug.frequency }} veces</span>
                  </div>
                  <div class="flex items-center gap-1.5 text-indigo-600 dark:text-indigo-400">
                    <lucide-icon name="arrow-right-circle" class="w-3.5 h-3.5 shrink-0"></lucide-icon>
                    <span class="text-xs font-semibold truncate">{{ sug.suggestedAccount }}</span>
                  </div>
                </div>
              </div>
              
              <div class="flex items-center gap-2 mt-auto pt-2 border-t border-slate-100 dark:border-slate-800">
                <button (click)="accept(sug.id)" [disabled]="isProcessing(sug.id)"
                        class="flex-1 bg-indigo-50 hover:bg-indigo-100 dark:bg-indigo-500/10 dark:hover:bg-indigo-500/20 text-indigo-700 dark:text-indigo-300 text-xs font-medium py-1.5 rounded transition-colors flex items-center justify-center gap-1 disabled:opacity-50">
                  @if (isProcessing(sug.id)) {
                    <lucide-icon name="loader-2" class="w-3.5 h-3.5 animate-spin"></lucide-icon>
                  } @else {
                    <lucide-icon name="check" class="w-3.5 h-3.5"></lucide-icon> Aceptar
                  }
                </button>
                <button (click)="reject(sug.id)" [disabled]="isProcessing(sug.id)"
                        title="Ignorar esta sugerencia"
                        class="w-8 shrink-0 bg-slate-50 hover:bg-rose-50 dark:bg-slate-800 dark:hover:bg-rose-500/10 text-slate-400 hover:text-rose-600 dark:hover:text-rose-400 text-xs py-1.5 rounded transition-colors flex items-center justify-center disabled:opacity-50">
                  <lucide-icon name="x" class="w-3.5 h-3.5"></lucide-icon>
                </button>
              </div>
            </div>
          }
        </div>
      }
    </div>
  `
})
export class RuleSuggestionsList {
  private ruleService = inject(RuleService);
  private companyService = inject(CompanyService);
  private toast = inject(ToastService);

  suggestions = signal<RuleSuggestion[]>([]);
  processingIds = signal<Set<string>>(new Set());
  isRecalculating = signal(false);

  ruleAccepted = output();

  constructor() {
    effect(() => {
      const company = this.companyService.activeCompany();
      this.ruleService.suggestionRefreshTick(); // re-run when external refresh triggered
      if (company) {
        this.loadSuggestions(company.id);
      } else {
        this.suggestions.set([]);
      }
    });
  }

  isProcessing(id: string): boolean {
    return this.processingIds().has(id);
  }

  loadSuggestions(companyId: string) {
    this.ruleService.getSuggestions(companyId).subscribe({
      next: res => this.suggestions.set(res),
      error: () => {}
    });
  }

  accept(id: string) {
    const cid = this.companyService.activeCompany()?.id;
    if (!cid) return;

    this.setProcessing(id, true);
    this.ruleService.acceptSuggestion(cid, id).subscribe({
      next: ({ updatedTransactionCount }) => {
        this.removeSuggestion(id);
        this.ruleService.removeGlobalSuggestion(id);
        this.ruleService.triggerSuggestionRefresh();
        if (updatedTransactionCount > 0) this.ruleService.triggerTransactionRefresh();
        this.ruleAccepted.emit();
        if (updatedTransactionCount > 0) {
          this.toast.success(`Regla creada y aplicada a ${updatedTransactionCount} movimiento${updatedTransactionCount !== 1 ? 's' : ''} pendiente${updatedTransactionCount !== 1 ? 's' : ''}.`);
        } else {
          this.toast.success('Regla creada. Se aplicará a los próximos movimientos que coincidan.');
        }
      },
      error: (err) => {
        this.setProcessing(id, false);
      }
    });
  }

  reject(id: string) {
    const cid = this.companyService.activeCompany()?.id;
    if (!cid) return;

    this.setProcessing(id, true);
    this.ruleService.rejectSuggestion(cid, id).subscribe({
      next: () => {
        this.removeSuggestion(id);
      },
      error: () => {
        this.setProcessing(id, false);
        this.toast.error('Error al ignorar la sugerencia.');
      }
    });
  }

  private setProcessing(id: string, isProcessing: boolean) {
    const set = new Set(this.processingIds());
    if (isProcessing) set.add(id);
    else set.delete(id);
    this.processingIds.set(set);
  }

  recalculate() {
    const cid = this.companyService.activeCompany()?.id;
    if (!cid) return;
    this.isRecalculating.set(true);
    this.ruleService.recalculateSuggestions(cid).subscribe({
      next: (res) => {
        this.isRecalculating.set(false);
        this.loadSuggestions(cid);
        if (res.newSuggestions > 0) {
          this.toast.success(`Se ${res.newSuggestions === 1 ? 'generó 1 nueva sugerencia' : `generaron ${res.newSuggestions} nuevas sugerencias`}.`);
        } else {
          this.toast.info('Análisis completado. No hay nuevos patrones.');
        }
      },
      error: () => {
        this.isRecalculating.set(false);
        this.toast.error('Error al recalcular sugerencias.');
      },
    });
  }

  private removeSuggestion(id: string) {
    this.suggestions.update(s => s.filter(x => x.id !== id));
    this.setProcessing(id, false);
  }
}
