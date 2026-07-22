# Landing Page y Páginas Legales — Vistas Públicas

Paso a Paso para Salir a la Venta
Una vez superada la auditoría, el Go-to-Market debería seguir este orden:

Paso 1: Entorno de Producción. Migrar la base de datos PostgreSQL, el backend y el build optimizado del frontend a los servidores productivos definitivos.

Paso 2: Smoke Testing Final. Realizar pruebas reales en producción creando usuarios de prueba, subiendo extractos y generando los asientos contables para confirmar que no hay caídas en el servidor.

Paso 3: Definición del Modelo de Negocio. Integrar la pasarela de pagos (por ejemplo, Mercado Pago o Stripe) y conectar el flujo de suscripciones que charlaron.

Paso 4: Legales y Landing Page. Redactar los Términos y Condiciones, Políticas de Privacidad y pulir la página de aterrizaje comercial para que los usuarios sin el sistema puedan conocer la herramienta.

Paso 5: Onboarding "Family & Friends". Darle acceso a ese familiar tuyo que trabaja en estudios contables y a los contactos interesados de la facultad para que sean los primeros beta testers reales. Su feedback inicial vale oro.

Paso 6: Marketing y Ventas. Empezar a darle movimiento a la cuenta de Instagram, contactar estudios contables por LinkedIn y hacer demostraciones reales del ahorro de tiempo que genera la plataforma.

> Documento de referencia para el lanzamiento comercial. Última revisión: 2026-07-21.

## 1. Resumen ejecutivo

Las vistas públicas de ContableAI (accesibles sin login) son cuatro: la **Landing Page**, las dos **páginas legales** (Términos y Condiciones, Política de Privacidad) y las pantallas de **autenticación** (login / recupero de contraseña). Todas son componentes Angular standalone con `ChangeDetectionStrategy.OnPush`, lazy-loaded, estilados con Tailwind (mobile-first, con modo oscuro vía clase `dark`).

**Estado:** la landing está lista a nivel técnico. Las páginas legales tienen cargado el **borrador de producción** (redactado el 2026-07-21 conforme a la Ley 25.326 y al modelo B2B): se recomienda una revisión final por abogado antes del lanzamiento.

## 2. Mapa de rutas públicas

Definidas en [`frontend/src/app/app.routes.ts`](../frontend/src/app/app.routes.ts). Ninguna pasa por `authGuard`.

| Ruta | Componente | Archivo |
|---|---|---|
| `/inicio` | `LandingPage` | `frontend/src/app/features/landing/pages/landing-page/` |
| `/terminos` | `TermsPage` | `frontend/src/app/features/legal/pages/terms-page/` |
| `/privacidad` | `PrivacyPage` | `frontend/src/app/features/legal/pages/privacy-page/` |
| `/login` | `LoginPage` | `frontend/src/app/features/auth/pages/login-page/` |
| `/forgot-password` | `ForgotPasswordPage` | `frontend/src/app/features/auth/pages/forgot-password-page/` |
| `/reset-password` | `ResetPasswordPage` | `frontend/src/app/features/auth/pages/reset-password-page/` |
| `**` (cualquier otra) | redirige a `/inicio` | — |

La raíz `/` es la app privada (requiere sesión); el usuario sin sesión es manejado por `authGuard`.

## 3. Estructura de la Landing Page

Archivo de vista: `frontend/src/app/features/landing/pages/landing-page/landing-page.html`
Lógica y textos dinámicos: `landing-page.ts` (mismo directorio).

Secciones, en orden:

1. **Nav / Header** (sticky): logo + botones "Ingresá" y "Solicitá tu prueba".
2. **Hero**: título principal, subtítulo, CTA primario ("Solicitá tu prueba" → `/login?register=1`) y CTA secundario (scroll a "Cómo funciona").
3. **Problema → Solución**: dos columnas comparativas.
4. **Features** (6 tarjetas): renderizadas desde el array `features` del `.ts`.
5. **Cómo funciona** (3 pasos, `id="como-funciona"`): desde el array `steps` del `.ts`.
6. **Pricing** (`id="precios"`): plan **Pro** (US$20/mes, features desde `proFeatures`) y **Enterprise** (contacto por mailto, features desde `enterpriseFeatures`).
7. **CTA final**: banner indigo con botón de registro.
8. **Footer**: copyright, links a `/terminos`, `/privacidad` y `/login`.

## 4. Dónde editar los textos (guía para el dueño del producto)

### 4.1 Landing — textos "fijos" (títulos, párrafos, CTAs)

Editar directamente en el HTML:
`frontend/src/app/features/landing/pages/landing-page/landing-page.html`

- Título y subtítulo del Hero, badge "Para estudios contables", leyenda "Sin tarjeta".
- Bloque Problema/Solución completo.
- Títulos de sección ("Todo lo que tu estudio necesita", "En 3 pasos", "Precios simples").
- Precio del plan Pro (**US$20/mes**, línea del bloque Pricing) y textos descriptivos de cada plan.
- Email de contacto Enterprise (hoy `agussala2003@gmail.com`, en el `href` del botón "Hablá con nosotros").
- Textos del CTA final y del footer.

### 4.2 Landing — textos "de listas" (features, pasos, planes)

Editar en el TypeScript (son arrays tipados, campos `title` / `desc` / `text`):
`frontend/src/app/features/landing/pages/landing-page/landing-page.ts`

- `features` — las 6 tarjetas de funcionalidades.
- `steps` — los 3 pasos de "Cómo funciona".
- `proFeatures` / `enterpriseFeatures` — los bullets de cada plan.

### 4.3 Páginas legales (borrador de producción cargado)

Los textos definitivos (borrador) ya están inyectados; cualquier ajuste se edita sección por sección en:

- Términos: `frontend/src/app/features/legal/pages/terms-page/terms-page.html` — 12 secciones: aceptación B2B, descripción del servicio (sugerencias/borradores), obligaciones, autorización de terceros, suscripciones y suspensión por falta de pago (402), limitación de responsabilidad, disponibilidad, propiedad intelectual, terminación, modificaciones, ley aplicable (CABA) y contacto.
- Privacidad: `frontend/src/app/features/legal/pages/privacy-page/privacy-page.html` — 11 secciones conforme Ley 25.326 / AAIP: roles Responsable/Encargado, datos recopilados, finalidad, OCR en memoria (staging 24 h), retención (jobs 30 días, auditoría 5 años con seudonimización), cesión, seguridad, derechos del titular (con leyenda AAIP), modificaciones y contacto.

Al editar, recordar actualizar la línea **"Última actualización: …"** de cada página. Email de contacto legal: `contacto@contableai.com`.

### 4.4 SEO / metadatos del sitio

`frontend/src/index.html`: `<title>`, `<meta name="description">` y etiquetas Open Graph (`og:title`, `og:description`, `og:image` — la imagen es `frontend/public/og-image.png`).

## 5. Estándares técnicos aplicados

- Componentes **standalone** con `ChangeDetectionStrategy.OnPush` y lazy loading (`loadComponent`).
- Tailwind mobile-first (breakpoints `sm:` / `md:` / `lg:`), dark mode por clase.
- HTML5 semántico: `<header>`, `<nav>`, `<main>`, `<section>`, `<footer>`; jerarquía `h1` → `h2` → `h3`.
- `lang="es"` declarado en `index.html`.
- No hay plugin `@tailwindcss/typography`: las páginas legales usan utilidades manuales (`space-y`, `list-disc`, etc.) en lugar de clases `prose`. Si se instala el plugin más adelante, se pueden simplificar.

## 6. Pendientes para el lanzamiento

- [x] Reemplazar lorem ipsum de `/terminos` y `/privacidad` por textos legales reales (borrador de producción cargado el 2026-07-21; **revisión final de abogado recomendada antes de publicar**).
- [ ] Confirmar precio y features de los planes en la sección Pricing.
- [ ] Reemplazar el email personal por un email corporativo en la **landing** (botón Enterprise, `mailto:agussala2003@gmail.com`); las páginas legales ya usan `contacto@contableai.com`. Verificar que esa casilla exista y reciba correo.
- [ ] Verificar que `og-image.png` y favicons estén actualizados con el branding final.
