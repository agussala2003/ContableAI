import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ConfigService } from '../config/config.service';

export interface ChartOfAccountItem {
  id: string;
  name: string;
  isGlobal: boolean;
}

@Injectable({ providedIn: 'root' })
export class ChartOfAccountService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get baseUrl(): string {
    return `${this.configService.config().apiUrl}/chart-of-accounts`;
  }

  /** Lista reactiva de cuentas (nombres), lista una vez al iniciar la app. */
  accounts = signal<ChartOfAccountItem[]>([]);

  /** Solo los nombres, conveniente para datalists y selects. */
  accountNames = signal<string[]>([]);

  constructor() {
    this.load();
  }

  load(): void {
    this.http.get<ChartOfAccountItem[]>(this.baseUrl).subscribe({
      next: list => {
        this.accounts.set(list);
        this.accountNames.set(list.map(a => a.name));
      },
      error: () => {
        // Fallback silencioso — la app sigue funcionando con lista vacía
        console.warn('[ChartOfAccountService] No se pudieron cargar las cuentas contables.');
      },
    });
  }

  create(name: string) {
    return this.http.post<ChartOfAccountItem>(this.baseUrl, { name });
  }

  delete(id: string) {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
