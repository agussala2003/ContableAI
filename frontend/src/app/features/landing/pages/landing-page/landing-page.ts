import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideAngularModule } from 'lucide-angular';

interface Feature {
  icon: string;
  title: string;
  desc: string;
}

interface PlanFeature {
  text: string;
}

@Component({
  selector: 'app-landing-page',
  standalone: true,
  imports: [RouterLink, LucideAngularModule],
  templateUrl: './landing-page.html',
})
export class LandingPage {
  readonly year = new Date().getFullYear();

  /** Scroll suave a una sección por id (los href="#..." los intercepta el router de Angular). */
  scrollTo(id: string): void {
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  readonly features: Feature[] = [
    { icon: 'landmark',        title: 'Parsers multi-banco',        desc: 'BBVA, Galicia, Santander, Macro, Nación, Credicoop, Mercado Pago, Ualá y más. Subí el PDF del extracto y listo.' },
    { icon: 'wand-sparkles',   title: 'Cruce AFIP / VEP automático', desc: 'Importá tus VEP y el sistema los cruza con los movimientos por importe y fecha. Se acabó el detalle a mano.' },
    { icon: 'book-check',      title: 'Asientos de partida doble',   desc: 'Genera el asiento Debe/Haber listo para exportar, con validación de períodos cerrados.' },
    { icon: 'sparkles',        title: 'Reglas + aprendizaje',        desc: '245+ reglas predefinidas y sugerencias que aprenden de tu clasificación manual repetida.' },
    { icon: 'file-spreadsheet', title: 'Exportá a tu sistema',       desc: 'CSV, Excel, Holistor y Bejerman, con códigos de cuenta externos. Se integra a tu flujo actual.' },
    { icon: 'layout-list',     title: 'Modo Excel',                  desc: 'Clasificá en lote con atajos de teclado, como en una planilla. Rápido y sin fricción.' },
  ];

  readonly steps = [
    { icon: 'upload',         title: 'Subí tus extractos',           desc: 'Arrastrá los PDF de cualquier banco. Se parsean automáticamente.' },
    { icon: 'wand-sparkles',  title: 'Clasificación + cruce AFIP',   desc: 'Las reglas clasifican y los VEP se cruzan solos contra los movimientos.' },
    { icon: 'file-down',      title: 'Exportá los asientos',         desc: 'Descargá el asiento listo para tu sistema contable. Cierre terminado.' },
  ];

  readonly proFeatures: PlanFeature[] = [
    { text: 'Hasta 15 empresas' },
    { text: 'Transacciones ilimitadas' },
    { text: 'Hasta 250 reglas por empresa' },
    { text: 'Todos los parsers de banco' },
    { text: 'Cruce AFIP / VEP automático' },
    { text: 'Exportación CSV, Excel, Holistor, Bejerman' },
    { text: 'Soporte por email' },
  ];

  readonly enterpriseFeatures: PlanFeature[] = [
    { text: 'Empresas ilimitadas' },
    { text: 'Todo lo de Pro, sin límites' },
    { text: 'Onboarding dedicado' },
    { text: 'Soporte prioritario' },
    { text: 'Integraciones a medida' },
  ];
}
