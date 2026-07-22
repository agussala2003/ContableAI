import {
  ApplicationConfig,
  importProvidersFrom,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { routes } from './app.routes';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { loadingInterceptor } from './core/interceptors/loading.interceptor';
import { errorInterceptor } from './core/interceptors/error.interceptor';
import { AuthService } from './core/services/auth.service';
import { LucideAngularModule } from 'lucide-angular';
import { APP_LUCIDE_ICONS } from './core/icons/lucide-icons';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor, loadingInterceptor, errorInterceptor])),
    // A-3: rehidrata la sesión desde la cookie HttpOnly (silent refresh) antes de que el
    // router evalúe los guards. Si no hay cookie válida, arranca como anónimo sin bloquear.
    provideAppInitializer(() => firstValueFrom(inject(AuthService).restoreSession())),
    importProvidersFrom(LucideAngularModule.pick(APP_LUCIDE_ICONS)),
  ],
};
