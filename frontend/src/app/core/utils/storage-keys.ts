/**
 * Claves de `localStorage` de la aplicación, con su equivalente previo al rebranding a PreSal.
 *
 * Están centralizadas por dos motivos: la clave de onboarding se leía/escribía en cuatro lugares
 * distintos con el string repetido, y la migración de abajo necesita el par viejo→nuevo como
 * única fuente de verdad (una lista duplicada se desincroniza en el primer rename que se olvide).
 */
export const STORAGE_KEYS = {
  onboardingDone:  'presal_onboarding_done',
  tourDone:        'presal_tour_done',
  activeCompanyId: 'presal_active_company_id',
} as const;

/** Pares `clave vieja` → `clave nueva` que resuelve {@link migrateLegacyStorageKeys}. */
const LEGACY_KEY_MAP: ReadonlyArray<readonly [string, string]> = [
  ['contableai_onboarding_done',   STORAGE_KEYS.onboardingDone],
  ['contableai_tour_done',         STORAGE_KEYS.tourDone],
  ['contableai_active_company_id', STORAGE_KEYS.activeCompanyId],
];

/**
 * Renombra las claves del prefijo `contableai_` al prefijo `presal_`.
 *
 * Sin esto, el rebranding le cambia el estado a todos los usuarios que ya venían usando el
 * sistema: vuelven a ver el modal de onboarding y el tour guiado, y —lo más molesto— pierden
 * la empresa activa que tenían seleccionada.
 *
 * Se invoca desde `main.ts` ANTES de `bootstrapApplication`, no desde un `provideAppInitializer`:
 * `CompanyService` y `TourService` leen su clave dentro del ciclo de arranque de Angular, así que
 * un initializer podría llegar tarde según el orden de instanciación. Fuera del bootstrap el
 * orden es determinístico.
 *
 * Es idempotente: en la segunda ejecución ya no queda ninguna clave vieja y no hace nada. Nunca
 * pisa un valor nuevo ya existente, para no revivir un valor legacy que el usuario ya reemplazó.
 */
export function migrateLegacyStorageKeys(): void {
  try {
    for (const [legacyKey, currentKey] of LEGACY_KEY_MAP) {
      const legacyValue = localStorage.getItem(legacyKey);
      if (legacyValue === null) continue;

      if (localStorage.getItem(currentKey) === null)
        localStorage.setItem(currentKey, legacyValue);

      localStorage.removeItem(legacyKey);
    }
  } catch {
    // localStorage puede lanzar en modo privado o con las cookies de terceros bloqueadas.
    // La migración es una comodidad, no un requisito: si falla, el usuario ve el onboarding
    // de nuevo, que es exactamente el estado en el que estaría sin este código.
  }
}
