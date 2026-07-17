import { Component, input } from '@angular/core';
import { Currency } from '../../../core/services/transaction';

/**
 * Pill compacto que indica la moneda de un importe. Se muestra EXCLUSIVAMENTE para USD;
 * para ARS no renderiza nada, para no ensuciar la UI de las cuentas que operan 100% en pesos.
 */
@Component({
  selector: 'app-currency-badge',
  standalone: true,
  template: `
    @if (currency() === 'USD') {
      <span
        class="inline-flex items-center px-1.5 py-0.5 rounded text-[9px] font-bold tracking-wide align-middle
               bg-sky-50 dark:bg-sky-500/10 text-sky-700 dark:text-sky-300
               border border-sky-200 dark:border-sky-500/30"
      >USD</span>
    }
  `,
})
export class CurrencyBadge {
  currency = input<Currency | null | undefined>(null);
}
