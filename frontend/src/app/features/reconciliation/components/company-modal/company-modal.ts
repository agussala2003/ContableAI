import { Component, output } from '@angular/core';
import { CompanyBar } from '../company-bar/company-bar';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-company-modal',
  standalone: true,
  imports: [CompanyBar, LucideAngularModule],
  templateUrl: './company-modal.html',
})
export class CompanyModal {

  close = output<void>();
}
