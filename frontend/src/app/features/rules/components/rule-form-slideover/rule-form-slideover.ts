import { Component, input, output } from '@angular/core';
import { LucideAngularModule } from 'lucide-angular';
import { Direction, RuleForm } from '../rules.types';

export interface RuleFormFieldChange {
  field: keyof RuleForm;
  value: string | number | boolean | Direction;
}

@Component({
  selector: 'app-rule-form-slideover',
  standalone: true,
  imports: [LucideAngularModule],
  templateUrl: './rule-form-slideover.html',
})
export class RuleFormSlideover {
  open = input<boolean>(false);
  title = input<string>('Nueva Regla');
  form = input<RuleForm>({
    keyword: '',
    targetAccount: '',
    direction: null,
    priority: 100,
    requiresTaxMatching: false,
  });
  isSaving = input<boolean>(false);
  isEditing = input<boolean>(false);
  applyRetroactive = input<boolean>(true);

  closeRequested = output<void>();
  saveRequested = output<void>();
  formFieldChanged = output<RuleFormFieldChange>();
  applyRetroactiveChange = output<boolean>();

  close(): void {
    this.closeRequested.emit();
  }

  save(): void {
    this.saveRequested.emit();
  }

  onTextChange(field: 'keyword' | 'targetAccount', event: Event): void {
    const target = event.target as HTMLInputElement;
    this.formFieldChanged.emit({ field, value: target.value });
  }

  onDirectionChange(event: Event): void {
    const target = event.target as HTMLSelectElement;
    this.formFieldChanged.emit({ field: 'direction', value: (target.value || null) as Direction });
  }

  onPriorityChange(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.formFieldChanged.emit({ field: 'priority', value: Number(target.value) || 100 });
  }

  onTaxMatchingChange(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.formFieldChanged.emit({ field: 'requiresTaxMatching', value: target.checked });
  }

  onApplyRetroactiveChange(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.applyRetroactiveChange.emit(target.checked);
  }
}
