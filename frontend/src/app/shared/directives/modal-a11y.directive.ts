import { AfterViewInit, Directive, ElementRef, OnDestroy, inject, output } from '@angular/core';

const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(', ');

/**
 * Accesibilidad de modales (A11y): aplicada al contenedor de un diálogo,
 * - atrapa el foco con Tab/Shift+Tab dentro del modal (focus trap),
 * - mueve el foco al abrir hacia el primer elemento interactivo,
 * - emite `closeRequested` al presionar Escape,
 * - restaura el foco al elemento que lo tenía antes de abrir.
 *
 * El host debe llevar además `role="dialog"` y `aria-modal="true"`.
 */
@Directive({
  selector: '[appModalA11y]',
  standalone: true,
})
export class ModalA11yDirective implements AfterViewInit, OnDestroy {
  /** Emitido al presionar Escape: el componente decide cómo cerrar. */
  closeRequested = output<void>();

  private host = inject<ElementRef<HTMLElement>>(ElementRef);
  private previouslyFocused: Element | null = null;

  private readonly onKeydown = (event: KeyboardEvent): void => {
    if (event.defaultPrevented) return;

    if (event.key === 'Escape') {
      event.stopPropagation();
      this.closeRequested.emit();
      return;
    }

    if (event.key === 'Tab') {
      this.trapTab(event);
    }
  };

  ngAfterViewInit(): void {
    this.previouslyFocused = document.activeElement;
    document.addEventListener('keydown', this.onKeydown);

    // El contenido del modal puede renderizarse en el mismo tick: diferir el foco inicial.
    queueMicrotask(() => {
      const root = this.host.nativeElement;
      if (root.contains(document.activeElement) && document.activeElement !== document.body) return;
      const focusables = this.getFocusables();
      if (focusables.length > 0) {
        focusables[0].focus();
      } else {
        root.tabIndex = -1;
        root.focus();
      }
    });
  }

  ngOnDestroy(): void {
    document.removeEventListener('keydown', this.onKeydown);
    const prev = this.previouslyFocused;
    if (prev instanceof HTMLElement && document.contains(prev)) {
      prev.focus();
    }
  }

  private getFocusables(): HTMLElement[] {
    return Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR))
      .filter(el => el.offsetParent !== null); // descarta elementos ocultos (display:none)
  }

  private trapTab(event: KeyboardEvent): void {
    const focusables = this.getFocusables();
    if (focusables.length === 0) {
      event.preventDefault();
      return;
    }
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    const active = document.activeElement;

    // Si el foco escapó del modal, traerlo de vuelta.
    if (!this.host.nativeElement.contains(active)) {
      event.preventDefault();
      first.focus();
      return;
    }
    if (event.shiftKey && active === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus();
    }
  }
}
