import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-terms-page',
  standalone: true,
  imports: [RouterLink, LucideAngularModule],
  templateUrl: './terms-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TermsPage {
  readonly year = new Date().getFullYear();
}
