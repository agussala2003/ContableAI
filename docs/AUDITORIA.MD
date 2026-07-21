¡Buenas! Qué excelente noticia que ya estén pensando en sacar la aplicación a la cancha. Te aclaro que soy Gemini, la IA de Google, pero me parece perfecto armarte toda la estrategia acá para que después le bajes las instrucciones precisas a Claude Code con su modelo Fable.

Lanzar una plataforma SaaS financiera que procesa extractos bancarios en PDF requiere que la infraestructura esté sólida. Vamos a dividir esto en dos partes: la auditoría técnica y el paso a paso del lanzamiento.

### Auditoría Integral del Sistema

Para que Claude Code audite el repositorio de manera efectiva, te sugiero que lo guíes área por área (no le tires todo el código junto). Aquí tienes el enfoque:

* **Seguridad Informática:** Pídele que revise la autenticación y la autorización en los endpoints de .NET. Es crítico que escanee vulnerabilidades OWASP y garantice el cifrado de la información de los clientes, tanto en tránsito (TLS) como en reposo.


* **Calidad del Código y Funcionalidad:** Haz que evalúe la cobertura de pruebas unitarias y de integración. Claude debe buscar deuda técnica, código duplicado en los servicios y verificar la correcta conexión de las APIs externas.


* **Rendimiento y Escalabilidad:** El procesamiento del OCR puede consumir bastantes recursos. Pídele que analice el uso de memoria RAM y CPU. Debe evaluar los tiempos de respuesta y cómo se comporta el backend ante un pico de usuarios concurrentes.


* **Arquitectura e Infraestructura:** Que audite el manejo de errores globales y la trazabilidad de los logs en C# para una rápida resolución de fallos. También debe evaluar la escalabilidad en la nube y las estrategias de respaldo de la base de datos.


* **Usabilidad (UX/UI):** Dile que revise los componentes en Angular para garantizar que la navegación sea intuitiva. También puede verificar la accesibilidad y el diseño responsivo para distintas pantallas.


* **Cumplimiento Legal (Compliance):** Aunque Claude audite código, asegúrate de cumplir con la Ley 25.326 de Protección de Datos Personales en Argentina. Además, pídele que verifique las licencias de código abierto de terceros para evitar problemas de copyright o licenciamiento.



---

### Paso a Paso para Salir a la Venta

Una vez superada la auditoría, el *Go-to-Market* debería seguir este orden:

* **Paso 1: Entorno de Producción.** Migrar la base de datos PostgreSQL, el backend y el build optimizado del frontend a los servidores productivos definitivos.
* **Paso 2: Smoke Testing Final.** Realizar pruebas reales en producción creando usuarios de prueba, subiendo extractos y generando los asientos contables para confirmar que no hay caídas en el servidor.
* **Paso 3: Definición del Modelo de Negocio.** Integrar la pasarela de pagos (por ejemplo, Mercado Pago o Stripe) y conectar el flujo de suscripciones que charlaron.
* **Paso 4: Legales y Landing Page.** Redactar los Términos y Condiciones, Políticas de Privacidad y pulir la página de aterrizaje comercial para que los usuarios sin el sistema puedan conocer la herramienta.
* **Paso 5: Onboarding "Family & Friends".** Darle acceso a ese familiar tuyo que trabaja en estudios contables y a los contactos interesados de la facultad para que sean los primeros *beta testers* reales. Su feedback inicial vale oro.
* **Paso 6: Marketing y Ventas.** Empezar a darle movimiento a la cuenta de Instagram, contactar estudios contables por LinkedIn y hacer demostraciones reales del ahorro de tiempo que genera la plataforma.

¿Te gustaría que redactemos el primer prompt exacto para pasarle a Claude Code y arrancar con la auditoría de Seguridad Informática?

---

# 🔒 Auditoría de Seguridad de la Información — Pendientes

> Anotado el 2026-07-20. Estado del grueso del plan: **implementado y con tests verdes**
> (173/173 backend). Acá quedan registrados **solo los pendientes** para resolver más adelante.

## Ya completado (referencia rápida)
Críticas **C-1** (Global Query Filters por tenant) y **C-2** (secretos fuera de config + docker
parametrizado). Altas **A-1** (password admin obligatoria en prod), **A-2** (`/hangfire` con rol
SystemAdmin), **A-3** (refresh tokens en cookie HttpOnly + access token 30 min + token en memoria
en el front). Medias **M-1** (`UseForwardedHeaders`), **M-2** (magic bytes en subida), **M-3**
(CUIT único por tenant), **M-4** (políticas `RequireStudioOwner` en backend + gating de UI).
Multi-moneda (ARS/USD) completo. **No hay que rehacer nada de esto.**

## ⏳ Pendiente en código — Vulnerabilidades Bajas (B-1)

- [ ] **B-1a · `AllowedHosts` restringido.**
  Hoy `"AllowedHosts": "*"` en [appsettings.json](../backend/src/ContableAI.API/appsettings.json#L8).
  → Fijarlo al host de producción (o sobreescribir con env var en Render).

- [ ] **B-1b · Enumeración de emails en registro.**
  `register` y `register-studio` responden `409 "Ya existe un usuario con ese email."`
  ([AuthHandlers.cs:87 y :135](../backend/src/ContableAI.Infrastructure/Features/Auth/AuthHandlers.cs#L87)),
  a diferencia de `forgot-password` que sí es constante.
  → Unificar a una respuesta genérica, o aceptar el trade-off UX conscientemente.

- [ ] **B-1c · Token de reset con CSPRNG.**
  Se genera con `Guid.NewGuid().ToString("N")`
  ([AuthHandlers.cs:185](../backend/src/ContableAI.Infrastructure/Features/Auth/AuthHandlers.cs#L185)),
  que no garantiza aleatoriedad criptográfica.
  → Usar `RandomNumberGenerator` (≥128 bits) y **guardar el hash** del token, no el valor en claro.
  La expiración de 1 h ya está bien.

## ⏳ Observaciones menores (no bloqueantes, para el radar)

- [ ] **Cookie de refresh cross-site (A-3).** Es `SameSite=None` (Vercel ↔ Render son cross-site);
  navegadores que bloquean cookies de terceros podrían desloguear al recargar.
  → Validar en staging: login → reload → sigue logueado.
- [ ] **Inconsistencia del tenant del admin.** `SeedExtensions` usa `StudioTenantId = "system"` y
  `TenantConstants.System` usa `"SYSTEM"`. No afecta la seguridad (el bypass de filtros se basa en
  el **rol**, no en el string), pero conviene unificar.

## 🔧 Acciones operativas manuales (fuera de código — AÚN SIN HACER)

- [ ] **🔴 Purga del historial de Git + rotación de secretos (C-2).**
  Los secretos **siguen siendo recuperables del historial** (`git log -S 'npg_T4uWy2QlEwAq'` y
  `-S 'kSV1...'` los encuentran en commits `80d6c94`, `b54b4ee`, `14d1acf`). Quitarlos de commits
  posteriores no los borra.
  1. Rotar como quemados: nueva password de Neon, nueva `Jwt__Key` (`openssl rand -base64 48`),
     nueva `RESEND_API_KEY`.
  2. Purgar con `git filter-repo --replace-text` o BFG → `git push --force` → re-clone del equipo.
- [ ] **⚙️ Variables de entorno en Render** (antes del próximo deploy):
  `SEED_ADMIN_PASSWORD` (≥12, sin ella la API no arranca por diseño), `ConnectionStrings__DefaultConnection`,
  `Jwt__Key`, `Smtp__Password`, `Frontend__BaseUrl`, `ASPNETCORE_ENVIRONMENT=Production`.
- [ ] **🍪 Verificar CORS/cookie**: que `Frontend__BaseUrl` coincida **exactamente** con el origen del
  front (CORS con credenciales exige origen explícito).
- [ ] **🗄️ Migraciones**: `CuitUniquePerTenant` y `AddRefreshTokens` se aplican solas al arrancar
  (`MigrateAsync`), o manual con `dotnet ef database update`.