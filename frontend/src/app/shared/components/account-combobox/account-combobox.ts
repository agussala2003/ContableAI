import { Component, ElementRef, HostListener, computed, inject, input, output, signal } from '@angular/core';
import { NgClass } from '@angular/common';
import { LucideAngularModule } from 'lucide-angular';

/**
 * Combobox de cuenta contable: input de texto visible + dropdown filtrable.
 * Reemplaza al `<select>` nativo (que esconde lo tipeado) y al `<datalist>`
 * (inconsistente entre navegadores). Permite tipear para filtrar, navegar con
 * teclado y, si `allowFreeText` está activo, confirmar un texto libre.
 */
@Component({
  selector: 'app-account-combobox',
  standalone: true,
  imports: [LucideAngularModule, NgClass],
  templateUrl: './account-combobox.html',
})
export class AccountCombobox {
  /** Lista de cuentas disponibles. */
  accounts = input<string[]>([]);
  /** Valor seleccionado actual. */
  value = input<string>('');
  placeholder = input<string>('Seleccionar cuenta…');
  /** Clases Tailwind para el input (permite adaptar el control a cada contexto). */
  inputClass = input<string>('');
  /** Si es true, al cerrar confirma el texto tipeado aunque no exista en la lista. */
  allowFreeText = input<boolean>(true);

  /** Emite el valor elegido (cuenta de la lista o texto libre confirmado). */
  valueChange = output<string>();

  private host = inject<ElementRef<HTMLElement>>(ElementRef);

  query = signal<string>('');
  isOpen = signal<boolean>(false);
  highlighted = signal<number>(0);

  private static readonly DEFAULT_INPUT_CLASS =
    'w-full px-3.5 py-2.5 pr-9 rounded-xl border border-slate-200 dark:border-slate-700 ' +
    'bg-slate-50 dark:bg-slate-800 text-slate-900 dark:text-white text-sm ' +
    'focus:outline-none focus:ring-2 focus:ring-indigo-500/40 focus:border-indigo-500 transition-all duration-150';

  effectiveInputClass = computed(() => this.inputClass() || AccountCombobox.DEFAULT_INPUT_CLASS);

  /** Texto mostrado: lo tipeado mientras está abierto, el valor confirmado si está cerrado. */
  displayValue = computed(() => (this.isOpen() ? this.query() : this.value() || ''));

  filtered = computed(() => {
    const q = AccountCombobox.normalize(this.query());
    const all = this.accounts();
    if (!q) return all;
    return all.filter(a => AccountCombobox.normalize(a).includes(q));
  });

  private static normalize(s: string): string {
    return s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase().trim();
  }

  onFocus(): void {
    this.query.set(this.value() || '');
    this.isOpen.set(true);
    this.highlighted.set(0);
  }

  onInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
    this.isOpen.set(true);
    this.highlighted.set(0);
  }

  select(account: string): void {
    this.query.set(account);
    this.isOpen.set(false);
    this.valueChange.emit(account);
  }

  onKeydown(event: KeyboardEvent): void {
    const items = this.filtered();
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (!this.isOpen()) { this.onFocus(); return; }
        this.highlighted.set(Math.min(this.highlighted() + 1, items.length - 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.highlighted.set(Math.max(this.highlighted() - 1, 0));
        break;
      case 'Enter':
        event.preventDefault();
        if (this.isOpen() && items.length > 0) this.select(items[this.highlighted()]);
        else this.commit();
        break;
      case 'Escape':
        this.isOpen.set(false);
        this.query.set(this.value() || '');
        break;
    }
  }

  @HostListener('document:mousedown', ['$event'])
  onDocumentMouseDown(event: MouseEvent): void {
    if (this.isOpen() && !this.host.nativeElement.contains(event.target as Node))
      this.commit();
  }

  /** Cierra el dropdown confirmando el texto tipeado (si se permite texto libre). */
  private commit(): void {
    this.isOpen.set(false);
    if (!this.allowFreeText()) return;
    const typed = this.query().trim();
    if (typed !== (this.value() || '')) this.valueChange.emit(typed);
  }
}
