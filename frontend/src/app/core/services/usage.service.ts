import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from '../config/config.service';

/**
 * Consumo del período de facturación en curso.
 *
 * Sale del ledger de consumo, que es distinto de la cuota de `/dashboard/limits`: la cuota mide
 * capacidad (¿cuántas empresas y reglas entran en el plan?) contando el stock vivo, y el consumo
 * mide hechos inmutables (¿cuántos extractos se procesaron?). Borrar movimientos baja la cuota
 * usada; no baja el consumo, porque el trabajo ya se hizo.
 */
export interface CurrentUsage {
  /** Período en formato `YYYY-MM`. */
  periodKey: string;
  /** Extractos procesados y facturables en el período. */
  statementsProcessed: number;
}

@Injectable({ providedIn: 'root' })
export class UsageService {
  private http = inject(HttpClient);
  private configService = inject(ConfigService);

  private get baseUrl(): string {
    return `${this.configService.config().apiUrl}/usage`;
  }

  getCurrent(): Observable<CurrentUsage> {
    return this.http.get<CurrentUsage>(`${this.baseUrl}/current`);
  }
}
