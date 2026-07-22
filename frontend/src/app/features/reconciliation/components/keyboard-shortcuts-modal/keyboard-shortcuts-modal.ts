import { Component, output } from '@angular/core';
import { LucideAngularModule } from 'lucide-angular';
import { ModalA11yDirective } from '../../../../shared/directives/modal-a11y.directive';

@Component({
  selector: 'app-keyboard-shortcuts-modal',
  standalone: true,
  imports: [LucideAngularModule, ModalA11yDirective],
  templateUrl: './keyboard-shortcuts-modal.html',
})
export class KeyboardShortcutsModal {
  close = output<void>();

  onClose(): void {
    this.close.emit();
  }
}
