import { Injectable, signal } from '@angular/core';
import { environment } from '../../../environments/environment';
import { AppConfig } from './app-config.model';

const DEFAULT_CONFIG: AppConfig = {
  apiUrl: environment.apiUrl,
  appVersion: environment.appVersion,
  requestTimeoutMs: environment.requestTimeoutMs,
  defaultToastDurationMs: environment.defaultToastDurationMs,
  exportCooldownMs: environment.exportCooldownMs,
};

@Injectable({ providedIn: 'root' })
export class ConfigService {
  private readonly _config = signal<AppConfig>(DEFAULT_CONFIG);
  readonly config = this._config.asReadonly();

  async loadConfig(): Promise<void> {
    try {
      const response = await fetch('/config.json', { cache: 'no-cache' });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const raw = (await response.json()) as Partial<AppConfig>;
      this._config.set({
        apiUrl: this.toNonEmptyString(raw.apiUrl, DEFAULT_CONFIG.apiUrl),
        appVersion: this.toNonEmptyString(raw.appVersion, DEFAULT_CONFIG.appVersion),
        requestTimeoutMs: this.toPositiveNumber(raw.requestTimeoutMs, DEFAULT_CONFIG.requestTimeoutMs),
        defaultToastDurationMs: this.toPositiveNumber(raw.defaultToastDurationMs, DEFAULT_CONFIG.defaultToastDurationMs),
        exportCooldownMs: this.toPositiveNumber(raw.exportCooldownMs, DEFAULT_CONFIG.exportCooldownMs),
      });
    } catch (error) {
      console.warn('[ConfigService] No se pudo cargar /config.json. Se usara environment.ts como fallback.', error);
      this._config.set(DEFAULT_CONFIG);
    }
  }

  private toNonEmptyString(value: unknown, fallback: string): string {
    return typeof value === 'string' && value.trim().length > 0 ? value.trim() : fallback;
  }

  private toPositiveNumber(value: unknown, fallback: number): number {
    return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : fallback;
  }
}
