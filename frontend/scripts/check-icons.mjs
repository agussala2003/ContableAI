/**
 * Verifica que todo ícono referenciado en un template esté registrado en APP_LUCIDE_ICONS.
 *
 * El set de íconos está curado a mano (LucideAngularModule.pick) para no arrastrar las ~1500
 * de lucide al bundle. La contra es que un nombre no registrado NO rompe la compilación: falla
 * en runtime, al renderizar, y se lleva puesta la vista entera. Ya pasó tres veces —"more-vertical"
 * al agregar el menú kebab, y los legacy "check-circle"/"help-circle" heredados de lucide v1—.
 *
 * `ng build` no puede detectarlo: los nombres son strings en el HTML. Por eso este chequeo.
 *
 * Uso: npm run check:icons
 */
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const ICONS_FILE = 'src/app/core/icons/lucide-icons.ts';

/** PascalCase → kebab-case, con el corte también entre letra y dígito (BarChart3 → bar-chart-3). */
const toKebab = (name) =>
  name
    .replace(/([a-z])([A-Z])/g, '$1-$2')
    .replace(/([A-Za-z])([0-9])/g, '$1-$2')
    .replace(/([A-Z])([A-Z][a-z])/g, '$1-$2')
    .toLowerCase();

function templateFiles(dir, acc = []) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) templateFiles(full, acc);
    else if (/\.(html|ts)$/.test(entry.name)) acc.push(full);
  }
  return acc;
}

// Solo el objeto exportado: la lista de imports de arriba tiene la misma forma y contarla dos
// veces daría un set correcto por casualidad, no por construcción.
const registered = new Set(
  [...readFileSync(ICONS_FILE, 'utf8').split('export const APP_LUCIDE_ICONS')[1]
    .matchAll(/^\s*([A-Z][A-Za-z0-9]*),/gm)].map((m) => toKebab(m[1])),
);

const used = new Map();
for (const file of templateFiles('src')) {
  const source = readFileSync(file, 'utf8');
  // Forma estática (name="x") y la literal en binding ([name]="'x'"). Un [name] calculado no se
  // puede verificar estáticamente; no hay ninguno hoy.
  for (const re of [/<lucide-icon[^>]*?\sname="([a-z0-9-]+)"/g, /\[name\]="'([a-z0-9-]+)'"/g]) {
    for (const m of source.matchAll(re)) {
      if (!used.has(m[1])) used.set(m[1], new Set());
      used.get(m[1]).add(file);
    }
  }
}

const missing = [...used].filter(([name]) => !registered.has(name));

if (missing.length === 0) {
  console.log(`✔ ${used.size} íconos usados, todos registrados (${registered.size} disponibles).`);
  process.exit(0);
}

console.error(`✘ ${missing.length} ícono(s) sin registrar en ${ICONS_FILE}:\n`);
for (const [name, files] of missing) {
  console.error(`  "${name}"`);
  for (const f of files) console.error(`      ${f}`);
}
console.error('\nAgregalos a APP_LUCIDE_ICONS, o usá el nombre actual del ícono en lucide.');
process.exit(1);
