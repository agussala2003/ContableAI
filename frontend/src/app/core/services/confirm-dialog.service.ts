import { Injectable, signal } from '@angular/core';

export interface ConfirmDialogConfig {
  title: string;
  message: string;
  confirmLabel?: string;
}

@Injectable({ providedIn: 'root' })
export class ConfirmDialogService {
  isVisible  = signal(false);
  config     = signal<ConfirmDialogConfig>({ title: '', message: '' });

  private _resolve: ((value: boolean) => void) | null = null;

  /**
   * Muestra el modal de confirmación y devuelve una Promise<boolean>.
   * true = usuario confirmó, false = usuario canceló.
   */
  confirm(config: ConfirmDialogConfig): Promise<boolean> {
    this.config.set(config);
    this.isVisible.set(true);
    return new Promise<boolean>(resolve => {
      this._resolve = resolve;
    });
  }

  /** Llamado por el componente cuando el usuario presiona "Confirmar". */
  accept(): void {
    this.isVisible.set(false);
    this._resolve?.(true);
    this._resolve = null;
  }

  /** Llamado por el componente cuando el usuario presiona "Cancelar". */
  cancel(): void {
    this.isVisible.set(false);
    this._resolve?.(false);
    this._resolve = null;
  }
}
