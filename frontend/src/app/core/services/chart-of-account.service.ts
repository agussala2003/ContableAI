import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ConfigService } from '../config/config.service';

export interface ChartOfAccountItem {
  id: string;
  name: string;
  isGlobal: boolean;
  isActive: boolean;
  externalCode?: string | null;
}

@Injectable({ providedIn: 'root' })
export class ChartOfAccountService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get baseUrl(): string {
    return `${this.configService.config().apiUrl}/chart-of-accounts`;
  }

  /** Lista reactiva de todas las cuentas (activas e inactivas para la pantalla de admin). */
  accounts = signal<ChartOfAccountItem[]>([]);

  /** Solo los nombres de cuentas ACTIVAS, para datalists y selects de la grilla. */
  accountNames = signal<string[]>([]);

  constructor() {
    this.load();
  }

  load(includeInactive = false): void {
    const url = includeInactive ? `${this.baseUrl}?includeInactive=true` : this.baseUrl;
    this.http.get<ChartOfAccountItem[]>(url).subscribe({
      next: list => {
        this.accounts.set(list);
        this.accountNames.set(list.filter(a => a.isActive).map(a => a.name));
      },
      error: () => {},
    });
  }

  create(name: string, externalCode?: string) {
    return this.http.post<ChartOfAccountItem>(this.baseUrl, { name, externalCode: externalCode ?? null });
  }

  delete(id: string) {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

  deactivate(id: string) {
    return this.http.patch<ChartOfAccountItem>(`${this.baseUrl}/${id}/deactivate`, {});
  }

  activate(id: string) {
    return this.http.patch<ChartOfAccountItem>(`${this.baseUrl}/${id}/activate`, {});
  }

  updateExternalCode(id: string, externalCode: string | null) {
    return this.http.patch<ChartOfAccountItem>(
      `${this.baseUrl}/${id}/external-code`,
      { externalCode }
    );
  }

  importExcel(file: File) {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<{ message: string, imported: number }>(`${this.baseUrl}/import`, formData);
  }
}
