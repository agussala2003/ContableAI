import { Component, inject, output, signal } from '@angular/core';
import { LucideAngularModule } from 'lucide-angular';
import { RuleService } from '../../services/rule.service';
import { CompanyService } from '../../services/company.service';
import { ToastService } from '../../services/toast.service';

@Component({
  selector: 'app-suggestions-drawer',
  standalone: true,
  imports: [LucideAngularModule],
  template: `
    <div class="h-full flex flex-col bg-white dark:bg-slate-900 shadow-2xl overflow-hidden">

      <!-- Header -->
      <div class="flex items-center gap-2 px-4 py-3.5 border-b border-slate-200 dark:border-slate-700/70 shrink-0">
        <lucide-icon name="sparkles" [size]="16" class="text-indigo-500 dark:text-indigo-400 shrink-0" />
        <h2 class="flex-1 text-sm font-semibold text-slate-900 dark:text-white">Aprendizaje Proactivo</h2>

        @if (suggestions().length > 0) {
          <span class="bg-indigo-100 dark:bg-indigo-900/60 text-indigo-700 dark:text-indigo-300 text-[10px] font-bold px-2 py-0.5 rounded-full">
            {{ suggestions().length }}
          </span>
        }

        <button (click)="recalculate()" [disabled]="isRecalculating()"
                title="Analizar clasificaciones y generar nuevas sugerencias"
                class="p-1.5 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-400 hover:text-slate-600
                       dark:hover:text-slate-300 disabled:opacity-40 transition-colors">
          <lucide-icon [name]="isRecalculating() ? 'loader-2' : 'refresh-cw'" [size]="14"
                       [class.animate-spin]="isRecalculating()" />
        </button>

        <button (click)="closed.emit()" title="Cerrar (Esc)"
                class="p-1.5 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-400 hover:text-slate-600
                       dark:hover:text-slate-300 transition-colors">
          <lucide-icon name="x" [size]="16" />
        </button>
      </div>

      <!-- Body -->
      <div class="flex-1 overflow-y-auto">
        @if (suggestions().length > 0) {
          <div class="p-4 space-y-3">
            <p class="text-xs text-slate-500 dark:text-slate-400 leading-relaxed">
              Patrones detectados en tus clasificaciones manuales. Aceptar crea una regla automática y la aplica a los movimientos pendientes.
            </p>

            @for (sug of suggestions(); track sug.id) {
              <div class="bg-slate-50 dark:bg-slate-800 border border-slate-200 dark:border-slate-700/60 rounded-xl p-3.5">
                <div class="mb-3">
                  <div class="flex items-center gap-1.5 mb-1.5">
                    <span class="text-xs font-semibold text-slate-800 dark:text-slate-200 truncate">{{ sug.keyword }}</span>
                    <span class="shrink-0 text-[10px] bg-white dark:bg-slate-700 border border-slate-200 dark:border-slate-600
                                 text-slate-500 dark:text-slate-400 px-1.5 py-0.5 rounded font-mono">
                      {{ sug.frequency }}×
                    </span>
                  </div>
                  <div class="flex items-center gap-1.5 text-indigo-600 dark:text-indigo-400">
                    <lucide-icon name="arrow-right-circle" [size]="13" class="shrink-0" />
                    <span class="text-xs font-medium truncate">{{ sug.suggestedAccount }}</span>
                  </div>
                </div>

                <div class="flex gap-2">
                  <button (click)="accept(sug.id)" [disabled]="isProcessing(sug.id)"
                          class="flex-1 flex items-center justify-center gap-1.5 py-1.5 px-3 rounded-lg text-xs font-semibold
                                 bg-indigo-600 hover:bg-indigo-700 text-white disabled:opacity-50 transition-colors shadow-sm">
                    @if (isProcessing(sug.id)) {
                      <lucide-icon name="loader-2" [size]="13" class="animate-spin" />
                    } @else {
                      <lucide-icon name="check" [size]="13" />
                    }
                    Aceptar regla
                  </button>
                  <button (click)="reject(sug.id)" [disabled]="isProcessing(sug.id)"
                          title="Ignorar esta sugerencia"
                          class="flex items-center justify-center w-9 rounded-lg text-xs
                                 bg-white dark:bg-slate-700 border border-slate-200 dark:border-slate-600
                                 hover:bg-rose-50 dark:hover:bg-rose-500/10 text-slate-400 hover:text-rose-500
                                 dark:hover:text-rose-400 disabled:opacity-50 transition-colors">
                    <lucide-icon name="x" [size]="13" />
                  </button>
                </div>
              </div>
            }
          </div>
        } @else {
          <div class="flex flex-col items-center justify-center h-full py-16 px-6 text-center">
            <div class="w-14 h-14 rounded-2xl bg-slate-100 dark:bg-slate-800 flex items-center justify-center mb-4">
              <lucide-icon name="sparkles" [size]="24" class="text-slate-400 dark:text-slate-500" />
            </div>
            <p class="text-sm font-semibold text-slate-600 dark:text-slate-300 mb-1">Sin sugerencias pendientes</p>
            <p class="text-xs text-slate-400 dark:text-slate-500 leading-relaxed max-w-52">
              El sistema detectará patrones cuando clasifiques manualmente al menos 3 movimientos similares.
            </p>
          </div>
        }
      </div>
    </div>
  `,
})
export class SuggestionsDrawer {
  closed = output<void>();

  private ruleService    = inject(RuleService);
  private companyService = inject(CompanyService);
  private toast          = inject(ToastService);

  suggestions    = this.ruleService.globalSuggestions;
  isRecalculating = signal(false);
  processingIds   = signal<Set<string>>(new Set());

  isProcessing(id: string): boolean {
    return this.processingIds().has(id);
  }

  accept(id: string): void {
    const cid = this.companyService.activeCompany()?.id;
    if (!cid) return;

    this.setProcessing(id, true);
    this.ruleService.acceptSuggestion(cid, id).subscribe({
      next: ({ updatedTransactionCount }) => {
        this.ruleService.removeGlobalSuggestion(id);
        this.ruleService.triggerSuggestionRefresh();
        if (updatedTransactionCount > 0) this.ruleService.triggerTransactionRefresh();
        this.setProcessing(id, false);
        const msg = updatedTransactionCount > 0
          ? `Regla creada y aplicada a ${updatedTransactionCount} movimiento${updatedTransactionCount !== 1 ? 's' : ''}.`
          : 'Regla creada. Se aplicará a los próximos movimientos que coincidan.';
        this.toast.success(msg);
      },
      error: () => this.setProcessing(id, false),
    });
  }

  reject(id: string): void {
    const cid = this.companyService.activeCompany()?.id;
    if (!cid) return;

    this.setProcessing(id, true);
    this.ruleService.rejectSuggestion(cid, id).subscribe({
      next: () => {
        this.ruleService.removeGlobalSuggestion(id);
        this.setProcessing(id, false);
      },
      error: () => {
        this.setProcessing(id, false);
        this.toast.error('Error al ignorar la sugerencia.');
      },
    });
  }

  recalculate(): void {
    const cid = this.companyService.activeCompany()?.id;
    if (!cid) return;

    this.isRecalculating.set(true);
    this.ruleService.recalculateSuggestions(cid).subscribe({
      next: (res) => {
        this.isRecalculating.set(false);
        this.ruleService.loadGlobalSuggestions(cid);
        if (res.newSuggestions > 0) {
          const n = res.newSuggestions;
          this.toast.success(`Se ${n === 1 ? 'generó 1 nueva sugerencia' : `generaron ${n} nuevas sugerencias`}.`);
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

  private setProcessing(id: string, active: boolean): void {
    const set = new Set(this.processingIds());
    if (active) set.add(id); else set.delete(id);
    this.processingIds.set(set);
  }
}
