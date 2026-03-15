import { Component, output, input } from '@angular/core';
import { NgClass } from '@angular/common';
import { LucideAngularModule } from 'lucide-angular';

export interface OnboardingStep {
  n: string;
  key: 'company' | 'upload' | 'review' | 'export';
  title: string;
  desc: string;
  color: string;
}

@Component({
  selector: 'app-onboarding-modal',
  standalone: true,
  imports: [NgClass, LucideAngularModule],
  templateUrl: './onboarding-modal.html',
})
export class OnboardingModal {
  displayName = input<string>('');

  start = output<void>();
  skip  = output<void>();

  readonly steps: OnboardingStep[] = [
    { n: '1', key: 'company', title: 'Creá una empresa',              desc: 'Registrá el CUIT y el nombre del cliente. Podés tener varias.',                                  color: 'bg-indigo-100 dark:bg-indigo-500/20 text-indigo-600 dark:text-indigo-400' },
    { n: '2', key: 'upload',  title: 'Subí el extracto bancario',     desc: 'CSV, XLSX o PDF. El banco se detecta solo (BBVA, MercadoPago, Ualá y más).',                    color: 'bg-sky-100 dark:bg-sky-500/20 text-sky-600 dark:text-sky-400'             },
    { n: '3', key: 'review',  title: 'Revisá y asigná cuentas',       desc: 'Aplicá reglas, ajustá pendientes y validá los movimientos antes de asentar.',                  color: 'bg-amber-100 dark:bg-amber-500/20 text-amber-600 dark:text-amber-400'     },
    { n: '4', key: 'export',  title: 'Exportá el libro diario',       desc: 'Generá el asiento en doble entrada y exportá a Excel, Holistor o Bejerman.',                   color: 'bg-teal-100 dark:bg-teal-500/20 text-teal-600 dark:text-teal-400'         },
  ];

  onStart(): void {
    localStorage.setItem('contableai_onboarding_done', 'true');
    this.start.emit();
  }

  onSkip(): void {
    localStorage.setItem('contableai_onboarding_done', 'true');
    this.skip.emit();
  }
}
