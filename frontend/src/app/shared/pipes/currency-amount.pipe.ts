import { Pipe, PipeTransform, inject, LOCALE_ID } from '@angular/core';
import { formatNumber } from '@angular/common';
import { Currency } from '../../core/services/transaction';

/** Símbolo/código de moneda usado como prefijo del importe. */
function currencySymbol(currency?: Currency | null): string {
  return currency === 'USD' ? 'US$' : '$';
}

/**
 * Formatea un importe con su moneda integrada como prefijo (sin badges ni cajas de fondo).
 * Centraliza el formato para no repetirlo en las vistas.
 *
 * - ARS (o sin moneda): `$1,234.56` (formato actual, prefijo pegado).
 * - USD: `US$ 1,234.56`.
 *
 * El prefijo forma parte del mismo string, así hereda el color/estilo del número (verde/naranja
 * en la grilla) y se ve integrado, no como un elemento ajeno.
 */
@Pipe({ name: 'currencyAmount', standalone: true })
export class CurrencyAmountPipe implements PipeTransform {
  private locale = inject(LOCALE_ID);

  transform(amount: number | null | undefined, currency?: Currency | null): string {
    const formatted = formatNumber(amount ?? 0, this.locale, '1.2-2') ?? String(amount ?? 0);
    return currency === 'USD' ? `${currencySymbol(currency)} ${formatted}` : `${currencySymbol(currency)}${formatted}`;
  }
}

/**
 * Devuelve solo el símbolo/código de moneda (`$` o `US$`). Se usa donde el prefijo se renderiza
 * con estilo propio (ej. tarjetas del dashboard, donde la moneda va sutil y el número destaca).
 */
@Pipe({ name: 'currencySymbol', standalone: true })
export class CurrencySymbolPipe implements PipeTransform {
  transform(currency?: Currency | null): string {
    return currencySymbol(currency);
  }
}
