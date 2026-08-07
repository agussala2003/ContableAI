import { HttpResponse } from '@angular/common/http';

/**
 * Extrae el nombre de archivo del header `Content-Disposition` de una respuesta.
 *
 * El backend ya arma el nombre correcto (ej: `LibroDiario_Panaderia_Sur_SRL_03-2026.xlsx`),
 * así que la fuente de verdad es el servidor y el frontend solo lo lee. Requiere que la
 * política de CORS exponga el header (`WithExposedHeaders("Content-Disposition")` en
 * `ServiceExtensions.AddContableCors`); si no está expuesto, el navegador devuelve `null`
 * y se usa el `fallback`.
 *
 * Prioriza `filename*` (RFC 5987, percent-encoded UTF-8) sobre `filename`, porque ASP.NET Core
 * emite ambos cuando el nombre tiene caracteres no-ASCII y el `filename` plano viene con los
 * acentos degradados — frecuente en razones sociales argentinas ("Panadería", "Ñandú SRL").
 */
export function filenameFromResponse(response: HttpResponse<unknown>, fallback: string): string {
  const header = response.headers.get('Content-Disposition');
  if (!header) return fallback;

  const extended = /filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(header);
  if (extended?.[2]) {
    try {
      const decoded = decodeURIComponent(extended[2].trim());
      if (decoded) return decoded;
    } catch {
      // Percent-encoding inválido: seguimos con el filename plano.
    }
  }

  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(header);
  const name = plain?.[1]?.trim();
  return name || fallback;
}

/** Dispara la descarga de un blob en el navegador con el nombre indicado. */
export function saveBlob(blob: Blob, filename: string): void {
  const url  = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href     = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
}

/**
 * Descarga el cuerpo de una respuesta usando el nombre que informa el servidor,
 * con `fallback` cuando el header no está disponible.
 */
export function saveResponseAsFile(response: HttpResponse<Blob>, fallback: string): void {
  if (!response.body) return;
  saveBlob(response.body, filenameFromResponse(response, fallback));
}
