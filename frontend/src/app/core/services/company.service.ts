import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { ConfigService } from '../config/config.service';
import { STORAGE_KEYS } from '../utils/storage-keys';

export interface Company {
  id: string;
  name: string;
  cuit: string;
  businessType: string;
  isActive: boolean;
  splitChequeTax: boolean;
  bankAccountName: string;
  usdBankAccountName?: string | null;
}

export interface CreateCompanyRequest {
  name: string;
  cuit: string;
  businessType?: string;
  bankAccountName?: string;
  usdBankAccountName?: string | null;
}

export interface UpdateCompanyRequest {
  name?: string;
  businessType?: string;
  splitChequeTax?: boolean;
  bankAccountName?: string;
  usdBankAccountName?: string | null;
}

@Injectable({ providedIn: 'root' })
export class CompanyService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);
  private readonly activeCompanyKey = STORAGE_KEYS.activeCompanyId;

  private get apiUrl(): string {
    return `${this.configService.config().apiUrl}/companies`;
  }

  // Estado global: empresa seleccionada actualmente
  activeCompany = signal<Company | null>(null);
  companies = signal<Company[]>([]);

  /**
   * Id de la empresa activa. Es la dependencia que deben observar los effects que recargan datos:
   * `activeCompany` es un OBJETO y `loadCompanies()` lo reemplaza por una instancia nueva en cada
   * llamada —abrir el modal de empresas la dispara—, así que un effect atado al objeto se
   * re-ejecutaba, y lanzaba otra request, sin que la empresa hubiera cambiado. El computed compara
   * strings: solo emite cuando la empresa es realmente otra.
   */
  readonly activeCompanyId = computed(() => this.activeCompany()?.id ?? null);

  loadCompanies(): Observable<Company[]> {
    return this.http.get<Company[]>(this.apiUrl).pipe(
      tap(list => {
        this.companies.set(list);

        if (list.length === 0) {
          this.activeCompany.set(null);
          this.clearPersistedActiveCompanyId();
          return;
        }

        const persistedId = this.getPersistedActiveCompanyId();
        const persistedCompany = persistedId
          ? list.find(company => company.id === persistedId) ?? null
          : null;

        if (persistedCompany) {
          this.activeCompany.set(persistedCompany);
          return;
        }

        const currentId = this.activeCompany()?.id;
        const currentCompany = currentId
          ? list.find(company => company.id === currentId) ?? null
          : null;

        const nextActiveCompany = currentCompany ?? list[0];
        this.activeCompany.set(nextActiveCompany);
        this.persistActiveCompanyId(nextActiveCompany.id);
      })
    );
  }

  createCompany(req: CreateCompanyRequest): Observable<Company> {
    return this.http.post<Company>(this.apiUrl, req).pipe(
      tap(company => {
        this.companies.update(list => [...list, company]);
        this.activeCompany.set(company);
        this.persistActiveCompanyId(company.id);
      })
    );
  }

  selectCompany(company: Company): void {
    this.activeCompany.set(company);
    this.persistActiveCompanyId(company.id);
  }

  updateCompany(id: string, req: UpdateCompanyRequest): Observable<Company> {
    return this.http.put<Company>(`${this.apiUrl}/${id}`, req).pipe(
      tap(updated => {
        this.companies.update(list => list.map(c => c.id === id ? updated : c));
        if (this.activeCompany()?.id === id) {
          this.activeCompany.set(updated);
          this.persistActiveCompanyId(updated.id);
        }
      })
    );
  }

  deleteCompany(id: string): Observable<void> {
    return this.http.delete<void>(`${this.apiUrl}/${id}`).pipe(
      tap(() => {
        this.companies.update(list => list.filter(c => c.id !== id));
        if (this.activeCompany()?.id === id) {
          const remaining = this.companies();
          const next = remaining.length > 0 ? remaining[0] : null;
          this.activeCompany.set(next);
          if (next) {
            this.persistActiveCompanyId(next.id);
          } else {
            this.clearPersistedActiveCompanyId();
          }
        }
      })
    );
  }

  private getPersistedActiveCompanyId(): string | null {
    return localStorage.getItem(this.activeCompanyKey);
  }

  private persistActiveCompanyId(companyId: string): void {
    localStorage.setItem(this.activeCompanyKey, companyId);
  }

  private clearPersistedActiveCompanyId(): void {
    localStorage.removeItem(this.activeCompanyKey);
  }
}
