import { Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Company } from '../../../../core/services/company.service';
import { LucideAngularModule } from 'lucide-angular';
import { RuleFilterType } from '../rules.types';

@Component({
  selector: 'app-rules-toolbar',
  standalone: true,
  imports: [FormsModule, LucideAngularModule],
  templateUrl: './rules-toolbar.html',
})
export class RulesToolbar {
  companies = input<Company[]>([]);
  activeCompanyId = input<string | null>(null);
  rulesCount = input<number>(0);
  searchQuery = input<string>('');
  filterType = input<RuleFilterType>('all');
  showInactive = input<boolean>(false);
  canCreate = input<boolean>(false);

  companyChange = output<string>();
  manageCompaniesRequested = output<void>();
  searchQueryChange = output<string>();
  filterTypeChange = output<RuleFilterType>();
  showInactiveChange = output<boolean>();
  createRequested = output<void>();

  onCompanyChange(value: string): void {
    this.companyChange.emit(value);
  }

  onManageCompaniesClick(): void {
    this.manageCompaniesRequested.emit();
  }

  onSearchInput(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.searchQueryChange.emit(target.value);
  }

  onFilterTypeChange(event: Event): void {
    const target = event.target as HTMLSelectElement;
    this.filterTypeChange.emit(target.value as RuleFilterType);
  }

  onShowInactiveToggle(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.showInactiveChange.emit(target.checked);
  }

  onCreateClick(): void {
    this.createRequested.emit();
  }
}
