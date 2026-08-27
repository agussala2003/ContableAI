import { Component, inject, signal, OnInit } from '@angular/core';
import { NgClass } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { LucideAngularModule } from 'lucide-angular';
import { finalize } from 'rxjs';
import { ConfigService } from '../../../../core/config/config.service';
import { CurrentUsage, UsageService } from '../../../../core/services/usage.service';

export interface TenantQuotaResponse {
  plan: string;
  companiesUsed: number;
  maxCompanies: number;
  monthlyTransactionsUsed: number;
  maxMonthlyTransactions: number;
  totalRulesUsed: number;
  maxRules: number;
}

@Component({
  selector: 'app-settings-page',
  standalone: true,
  imports: [NgClass, LucideAngularModule],
  templateUrl: './settings-page.html',
})
export class SettingsPage implements OnInit {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);
  private usageService = inject(UsageService);

  quota = signal<TenantQuotaResponse | null>(null);
  loading = signal(true);

  /** Consumo del período en curso. `null` mientras carga o si el endpoint falló. */
  usage = signal<CurrentUsage | null>(null);

  ngOnInit() {
    this.http.get<TenantQuotaResponse>(`${this.configService.config().apiUrl}/dashboard/limits`)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe(res => this.quota.set(res));

    // El consumo se pide aparte del plan y no bloquea el `loading` de la pantalla: si el ledger
    // no responde, el usuario tiene que poder ver sus límites igual. La tarjeta simplemente no
    // aparece — es informativa, no condiciona ninguna acción.
    this.usageService.getCurrent().subscribe({
      next:  res => this.usage.set(res),
      error: ()  => this.usage.set(null),
    });
  }

  /** "2026-08" → "Agosto 2026". El backend manda la clave cruda; la traducción es de la vista. */
  periodLabel(periodKey: string): string {
    const months = [
      'Enero', 'Febrero', 'Marzo', 'Abril', 'Mayo', 'Junio',
      'Julio', 'Agosto', 'Septiembre', 'Octubre', 'Noviembre', 'Diciembre',
    ];

    const [year, month] = periodKey.split('-');
    const name = months[Number(month) - 1];

    return name ? `${name} ${year}` : periodKey;
  }
}
