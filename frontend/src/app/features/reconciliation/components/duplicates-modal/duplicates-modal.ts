import { Component, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import { LucideAngularModule } from 'lucide-angular';
import { SkippedDuplicate } from '../../../../core/services/transaction';
import { CurrencyAmountPipe } from '../../../../shared/pipes/currency-amount.pipe';
import { ModalA11yDirective } from '../../../../shared/directives/modal-a11y.directive';

@Component({
  selector: 'app-duplicates-modal',
  standalone: true,
  imports: [DatePipe, LucideAngularModule, CurrencyAmountPipe, ModalA11yDirective],
  templateUrl: './duplicates-modal.html',
})
export class DuplicatesModal {
  duplicates = input<SkippedDuplicate[]>([]);
  title      = input<string>('Registros omitidos');
  close      = output<void>();
}
