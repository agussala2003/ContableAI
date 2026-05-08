import { Component, inject, signal, OnInit } from '@angular/core';
import { NgClass } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { LucideAngularModule } from 'lucide-angular';
import { finalize } from 'rxjs';
import { ConfigService } from '../../../../core/config/config.service';

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

  quota = signal<TenantQuotaResponse | null>(null);
  loading = signal(true);

  ngOnInit() {
    this.http.get<TenantQuotaResponse>(`${this.configService.config().apiUrl}/dashboard/limits`)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe(res => this.quota.set(res));
  }
}
