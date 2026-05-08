import { Component, output } from '@angular/core';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-keyboard-shortcuts-modal',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './keyboard-shortcuts-modal.html',
})
export class KeyboardShortcutsModal {
  close = output<void>();

  onClose(): void {
    this.close.emit();
  }
}
