import { Injectable, inject, signal } from '@angular/core';
import { ConfigService } from '../config/config.service';

export type ToastType = 'success' | 'error' | 'warning';

export interface Toast {
  id: number;
  message: string;
  type: ToastType;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  private configService = inject(ConfigService);
  private _toasts = signal<Toast[]>([]);
  readonly toasts = this._toasts.asReadonly();

  private nextId = 0;

  /**
   * @param duration Milisegundos hasta el cierre automático. Con <c>0</c> (o menos) el toast queda
   * hasta que el usuario lo cierre: es para los avisos que exigen una acción en OTRA pantalla, que
   * un toast de cuatro segundos garantiza que se pierdan.
   */
  show(message: string, type: ToastType = 'success', duration?: number): void {
    const effectiveDuration = duration ?? this.configService.config().defaultToastDurationMs;
    const id = this.nextId++;
    this._toasts.update(list => [...list, { id, message, type }]);
    if (effectiveDuration > 0) setTimeout(() => this.dismiss(id), effectiveDuration);
  }

  /** Toast que no se va solo. El componente ya expone el botón de cierre. */
  persistent(message: string, type: ToastType = 'warning'): void {
    this.show(message, type, 0);
  }

  success(message: string, duration?: number): void {
    this.show(message, 'success', duration);
  }

  error(message: string, duration?: number): void {
    this.show(message, 'error', duration);
  }

  warning(message: string, duration?: number): void {
    this.show(message, 'warning', duration);
  }

  info(message: string, duration?: number): void {
    this.show(message, 'success', duration);
  }

  dismiss(id: number): void {
    this._toasts.update(list => list.filter(t => t.id !== id));
  }
}
