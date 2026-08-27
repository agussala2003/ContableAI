import { ChangeDetectionStrategy, Component } from '@angular/core';
import { NgClass } from '@angular/common';
import { RouterLink } from '@angular/router';
import { LucideAngularModule } from 'lucide-angular';

interface Feature {
  icon: string;
  title: string;
  desc: string;
}

/** Pack prepago de extractos publicado en la landing. */
interface StatementPack {
  name: string;
  statements: number;
  usd: number;
  perStatement: string;
  pitch: string;
  highlighted: boolean;
}

@Component({
  selector: 'app-landing-page',
  standalone: true,
  imports: [NgClass, RouterLink, LucideAngularModule],
  templateUrl: './landing-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
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

  /**
   * Packs prepagos. El precio es USD y NO se guarda ningún monto en pesos ni el tipo de cambio:
   * se cobra por transferencia al cambio del día. Los anclajes de referencia
   * ($6.000 / $12.000 / $27.000) se fijaron con el dólar a ~$1.550 el 27-08-2026; hardcodear
   * esos pesos dejaría publicado un precio que en semanas ya no rige.
   *
   * DEBE mantenerse en sintonía con los packs de `settings-page` y con los del modal de carga de
   * saldo en `admin-page`: si divergen, el cliente ve un precio y el admin carga otro.
   */
  readonly packs: StatementPack[] = [
    {
      name: 'Básico',
      statements: 20,
      usd: 4,
      perStatement: '0,20',
      pitch: 'Para probar el sistema con un cliente, sin comprometer mucho.',
      highlighted: false,
    },
    {
      name: 'Estudio',
      statements: 50,
      usd: 8,
      perStatement: '0,16',
      pitch: 'Alcanza para cerrar el mes de varios clientes chicos.',
      highlighted: true,
    },
    {
      name: 'Volumen',
      statements: 150,
      usd: 17,
      perStatement: '0,11',
      pitch: 'El mejor precio por extracto, comprando por adelantado.',
      highlighted: false,
    },
  ];

  /** Mail con el pack ya escrito en el asunto: menos fricción y menos pedidos ambiguos. */
  packMailto(pack: StatementPack): string {
    const subject = `Compra pack ${pack.name} (${pack.statements} extractos) - PreSal`;
    return `mailto:presalsoporte@gmail.com?subject=${encodeURIComponent(subject)}`;
  }
}
