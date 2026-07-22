import { Component, output } from '@angular/core';
import { CompanyBar } from '../company-bar/company-bar';
import { LucideAngularModule } from 'lucide-angular';
import { ModalA11yDirective } from '../../../../shared/directives/modal-a11y.directive';

@Component({
  selector: 'app-company-modal',
  standalone: true,
  imports: [CompanyBar, LucideAngularModule, ModalA11yDirective],
  templateUrl: './company-modal.html',
})
export class CompanyModal {

  close = output<void>();
}
