import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { migrateLegacyStorageKeys } from './app/core/utils/storage-keys';

// Antes del bootstrap: los servicios que leen `localStorage` se instancian durante el arranque
// de Angular, así que la migración de claves `contableai_*` → `presal_*` tiene que estar hecha
// para cuando eso ocurra.
migrateLegacyStorageKeys();

bootstrapApplication(App, appConfig)
  .catch((err) => console.error(err));
