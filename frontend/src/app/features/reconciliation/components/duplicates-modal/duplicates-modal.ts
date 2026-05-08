import { Component, input, output } from '@angular/core';
import { DecimalPipe, DatePipe } from '@angular/common';
import { LucideAngularModule } from 'lucide-angular';
import { SkippedDuplicate } from '../../../../core/services/transaction';

@Component({
  selector: 'app-duplicates-modal',
  standalone: true,
  imports: [DecimalPipe, DatePipe, LucideAngularModule],
  templateUrl: './duplicates-modal.html',
})
export class DuplicatesModal {
  duplicates = input<SkippedDuplicate[]>([]);
  title      = input<string>('Registros omitidos');
  close      = output<void>();
}
