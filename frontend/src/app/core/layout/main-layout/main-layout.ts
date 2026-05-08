import { Component, inject, signal, computed, effect, HostListener, OnInit, DestroyRef } from '@angular/core';
import { toSignal, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterOutlet, RouterLink, RouterLinkActive, Router, NavigationEnd } from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { ToastComponent } from '../../components/toast/toast.component';
import { ConfirmDialogComponent } from '../../components/confirm-dialog/confirm-dialog.component';
import { SuggestionsDrawer } from '../../components/suggestions-drawer/suggestions-drawer';
import { TourOverlay } from '../../components/tour-overlay/tour-overlay';
import { KeyboardShortcutsModal } from '../../../features/reconciliation/components/keyboard-shortcuts-modal/keyboard-shortcuts-modal';
import { CompanyService } from '../../services/company.service';
import { AuthService } from '../../services/auth.service';
import { LoadingService } from '../../services/loading.service';
import { RuleService } from '../../services/rule.service';
import { KeyboardService } from '../../services/keyboard.service';
import { TourService } from '../../services/tour.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-main-layout',
  standalone: true,
  imports: [
    RouterOutlet, RouterLink, RouterLinkActive,
    ToastComponent, ConfirmDialogComponent,
    SuggestionsDrawer,
    TourOverlay,
    KeyboardShortcutsModal,
    LucideAngularModule,
  ],
  templateUrl: './main-layout.html',
})
export class MainLayout implements OnInit {
  isDark                  = signal(localStorage.getItem('theme') !== 'light');
  sidebarOpen             = signal(localStorage.getItem('sidebar') !== 'closed');
  showSuggestionsDrawer   = signal(false);
  companyService          = inject(CompanyService);
  authService             = inject(AuthService);
  loadingService          = inject(LoadingService);
  ruleService             = inject(RuleService);
  keyboardService         = inject(KeyboardService);
  tourService             = inject(TourService);
  private router          = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private currentPath = toSignal(
    this.router.events.pipe(
      filter(e => e instanceof NavigationEnd),
      map(() => this.router.url.split('?')[0]),
      startWith(this.router.url.split('?')[0]),
    )
  );

  hasTour = computed(() => {
    const path = this.currentPath();
    return path ? this.tourService.hasTour(path) : false;
  });

  constructor() {
    // Reload global suggestion count whenever the active company changes
    effect(() => {
      const company = this.companyService.activeCompany();
      if (company) {
        this.ruleService.loadGlobalSuggestions(company.id);
      } else {
        this.ruleService.clearGlobalSuggestions();
      }
    });

    // Keep drawer in sync whenever any component triggers a suggestion refresh
    effect(() => {
      const tick = this.ruleService.suggestionRefreshTick();
      const company = this.companyService.activeCompany();
      if (tick > 0 && company) {
        this.ruleService.loadGlobalSuggestions(company.id);
      }
    });

    // Open suggestions drawer when keyboard shortcut G+S is pressed
    effect(() => {
      const tick = this.keyboardService.openSuggestionsTick();
      if (tick > 0) this.showSuggestionsDrawer.set(true);
    });
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.showSuggestionsDrawer()) {
      this.showSuggestionsDrawer.set(false);
    }
  }

  get isStudioOwner() {
    return this.authService.isStudioOwnerOrAdmin();
  }

  get isSystemAdmin() {
    return this.authService.isSystemAdmin();
  }

  ngOnInit() {
    if (this.isDark()) {
      document.documentElement.classList.add('dark');
    }
    setTimeout(() => this.companyService.loadCompanies().pipe(takeUntilDestroyed(this.destroyRef)).subscribe());
  }

  toggleTheme() {
    this.isDark.update(v => !v);
    if (this.isDark()) {
      document.documentElement.classList.add('dark');
      localStorage.setItem('theme', 'dark');
    } else {
      document.documentElement.classList.remove('dark');
      localStorage.setItem('theme', 'light');
    }
  }

  toggleSidebar() {
    this.sidebarOpen.update(v => !v);
    localStorage.setItem('sidebar', this.sidebarOpen() ? 'open' : 'closed');
  }

  startTour(): void {
    const path = this.router.url.split('?')[0];
    this.tourService.start(path);
  }

  roleLabel(role: string): string {
    const labels: Record<string, string> = {
      SystemAdmin: 'Administrador',
      StudioOwner: 'Titular del Estudio',
      DataEntry:   'Carga de Datos',
    };
    return labels[role] ?? role;
  }

}
