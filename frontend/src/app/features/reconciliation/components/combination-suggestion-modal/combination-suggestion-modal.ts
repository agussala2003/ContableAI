import { Component, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { LucideAngularModule } from 'lucide-angular';
import { AfipService, AfipComboSuggestion } from '../../afip.service';
import { ToastService } from '../../../../core/services/toast.service';
import { CurrencyAmountPipe } from '../../../../shared/pipes/currency-amount.pipe';

/**
 * Modal de confirmación de cruces múltiples AFIP: muestra, por cada débito bancario sin
 * conciliar, las combinaciones de VEPs pendientes cuya sumatoria coincide exactamente y
 * pregunta al usuario si desea aplicarlas. La conciliación NUNCA se asienta sola: este modal
 * es el único camino de aplicación (UI custom, sin alertas nativas del navegador).
 */
@Component({
  selector: 'app-combination-suggestion-modal',
  standalone: true,
  imports: [DatePipe, LucideAngularModule, CurrencyAmountPipe],
  templateUrl: './combination-suggestion-modal.html',
})
export class CombinationSuggestionModal {
  private afipService = inject(AfipService);
  private toast = inject(ToastService);

  companyId   = input.required<string>();
  suggestions = input<AfipComboSuggestion[]>([]);

  /** Emitido tras aplicar con éxito una combinación (el padre refresca vouchers y grilla). */
  applied = output<string>();
  close   = output<void>();

  /** transactionId → índice de la alternativa elegida (0 por defecto). */
  selectedAlternative = signal<Record<string, number>>({});

  /** transactionId en proceso de aplicación (deshabilita sus botones). */
  applyingId = signal<string | null>(null);

  /** transactionIds ya aplicados en esta sesión del modal (se muestran como confirmados). */
  appliedIds = signal<Set<string>>(new Set());

  altIndexFor(transactionId: string): number {
    return this.selectedAlternative()[transactionId] ?? 0;
  }

  selectAlternative(transactionId: string, index: number) {
    this.selectedAlternative.update(sel => ({ ...sel, [transactionId]: index }));
  }

  isApplied(transactionId: string): boolean {
    return this.appliedIds().has(transactionId);
  }

  sumOf(suggestion: AfipComboSuggestion, altIndex: number): number {
    return suggestion.alternatives[altIndex]?.vouchers
      .reduce((acc, v) => acc + v.amount, 0) ?? 0;
  }

  apply(suggestion: AfipComboSuggestion) {
    if (this.applyingId() || this.isApplied(suggestion.transactionId)) return;

    const alt = suggestion.alternatives[this.altIndexFor(suggestion.transactionId)];
    if (!alt) return;

    this.applyingId.set(suggestion.transactionId);
    this.afipService
      .applyCombination(this.companyId(), suggestion.transactionId, alt.vouchers.map(v => v.id))
      .subscribe({
        next: (result) => {
          this.applyingId.set(null);
          this.appliedIds.update(ids => new Set(ids).add(suggestion.transactionId));
          this.toast.success(
            `Cruce aplicado: ${result.vouchersMatched} VEPs conciliados como "${result.assignedAccount}".`);
          this.applied.emit(suggestion.transactionId);
        },
        error: (err) => {
          this.applyingId.set(null);
          const detail = typeof err?.error === 'string' ? err.error : null;
          this.toast.error(detail ?? 'No se pudo aplicar la combinación. Actualizá y volvé a intentar.');
        },
      });
  }
}
