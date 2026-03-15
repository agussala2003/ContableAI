import { APP_INITIALIZER, ApplicationConfig, importProvidersFrom, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { loadingInterceptor } from './core/interceptors/loading.interceptor';
import { errorInterceptor } from './core/interceptors/error.interceptor';
import { ConfigService } from './core/config/config.service';
import { LucideAngularModule } from 'lucide-angular';
import { APP_LUCIDE_ICONS } from './core/icons/lucide-icons';

function initAppConfig(configService: ConfigService): () => Promise<void> {
  return () => configService.loadConfig();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor, loadingInterceptor, errorInterceptor])),
    importProvidersFrom(LucideAngularModule.pick(APP_LUCIDE_ICONS)),
    {
      provide: APP_INITIALIZER,
      useFactory: initAppConfig,
      deps: [ConfigService],
      multi: true,
    },
  ],
};
