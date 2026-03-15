import { Component, inject, signal, OnInit } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { ToastComponent } from '../../components/toast/toast.component';
import { ConfirmDialogComponent } from '../../components/confirm-dialog/confirm-dialog.component';
import { CompanyService } from '../../services/company.service';
import { AuthService } from '../../services/auth.service';
import { LoadingService } from '../../services/loading.service';
import { LucideAngularModule } from 'lucide-angular';

@Component({
  selector: 'app-main-layout',
  standalone: true,
  imports: [
    RouterOutlet, RouterLink, RouterLinkActive,
    ToastComponent, ConfirmDialogComponent,
    LucideAngularModule,
  ],
  templateUrl: './main-layout.html',
  styleUrl: './main-layout.scss',
})
export class MainLayout implements OnInit {
  isDark         = signal(false);
  sidebarOpen    = signal(true);
  companyService = inject(CompanyService);
  authService    = inject(AuthService);
  loadingService = inject(LoadingService);

  // Lucide icons

  get isStudioOwner() {
    return this.authService.isStudioOwnerOrAdmin();
  }

  get isSystemAdmin() {
    return this.authService.isSystemAdmin();
  }

  ngOnInit() {
    const saved = localStorage.getItem('theme');
    if (saved !== 'light') {
      document.documentElement.classList.add('dark');
      this.isDark.set(true);
    }
    if (localStorage.getItem('sidebar') === 'closed') {
      this.sidebarOpen.set(false);
    }

    // Restore persisted active company on every page load so that /journal
    // and /rules keep the selection after a browser refresh.
    this.companyService.loadCompanies().subscribe();
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

  roleLabel(role: string): string {
    const labels: Record<string, string> = {
      SystemAdmin: 'Administrador',
      StudioOwner: 'Titular del Estudio',
      DataEntry:   'Carga de Datos',
    };
    return labels[role] ?? role;
  }

}
