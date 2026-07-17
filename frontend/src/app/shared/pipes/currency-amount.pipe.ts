import { Pipe, PipeTransform, inject, LOCALE_ID } from '@angular/core';
import { formatNumber } from '@angular/common';
import { Currency } from '../../core/services/transaction';

/**
 * Formatea un importe con su moneda. Centraliza la lógica de formato para no repetirla en las
 * vistas (grilla, modales, detalle).
 *
 * - ARS (o sin moneda): conserva exactamente el formato actual con prefijo "$" (ej. "$1,234.56").
 * - USD: devuelve solo el número (ej. "1,234.56"); el indicador de moneda lo aporta el badge
 *   <app-currency-badge>, así no se ensucia la UI de las cuentas en pesos.
 */
@Pipe({ name: 'currencyAmount', standalone: true })
export class CurrencyAmountPipe implements PipeTransform {
  private locale = inject(LOCALE_ID);

  transform(amount: number | null | undefined, currency?: Currency | null): string {
    const formatted = formatNumber(amount ?? 0, this.locale, '1.2-2') ?? String(amount ?? 0);
    return currency === 'USD' ? formatted : `$${formatted}`;
  }
}
