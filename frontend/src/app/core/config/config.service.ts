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
  readonly config = signal<AppConfig>(DEFAULT_CONFIG).asReadonly();
}
