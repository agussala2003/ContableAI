import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-privacy-page',
  standalone: true,
  imports: [RouterLink, LucideAngularModule],
  templateUrl: './privacy-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PrivacyPage {
  readonly year = new Date().getFullYear();
}
