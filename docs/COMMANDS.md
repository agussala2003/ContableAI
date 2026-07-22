# ContableAI — Guía de Desarrollo y Operaciones

> Todos los comandos asumen que estás parado en la raíz del repo (`ContableAI/`)
> a menos que se indique lo contrario.
> Última actualización: 2026-05-07

---

## Índice

1. [Stack y entornos](#1-stack-y-entornos)
2. [Setup inicial](#2-setup-inicial)
3. [Levantar el proyecto localmente](#3-levantar-el-proyecto-localmente)
4. [Backend .NET](#4-backend-net)
5. [Frontend Angular](#5-frontend-angular)
6. [Migraciones EF Core](#6-migraciones-ef-core)
7. [Reset de base de datos](#7-reset-de-base-de-datos)
8. [Variables de entorno y secretos](#8-variables-de-entorno-y-secretos)
9. [Tests](#9-tests)
10. [Deploy](#10-deploy)
11. [Flujo de trabajo día a día](#11-flujo-de-trabajo-día-a-día)
12. [Checklists](#12-checklists)

---

## 1. Stack y entornos

### Stack

| Capa | Tecnología |
|------|-----------|
| Backend | .NET 10, Clean Architecture (API / Application / Domain / Infrastructure) |
| Frontend | Angular 21, Tailwind CSS 4 |
| Base de datos | PostgreSQL 16 |
| ORM | Entity Framework Core 10 + Npgsql |
| Jobs | Hangfire (dashboard en `/hangfire`) |
| Email | Resend (SMTP) |
| Auth | JWT Bearer |
| Logs | Serilog (archivos diarios + consola) |
| OCR | Tesseract 4 (español) |

### Entornos

| Entorno | API | Frontend | Base de datos |
|---------|-----|----------|---------------|
| **Local** | `http://localhost:5284` | `http://localhost:4200` | Docker `localhost:5432` |
| **Producción** | `https://contableai-api.onrender.com` | `https://contable-ai-sandy.vercel.app` | Neon PostgreSQL |

### Cómo .NET elige la configuración

ASP.NET Core carga los archivos en este orden (cada uno sobreescribe al anterior):

```
appsettings.json                ← base sin secretos (commiteado)
appsettings.{ENVIRONMENT}.json  ← overrides por entorno (Development gitignoreado)
Variables de entorno             ← máxima prioridad (formato: Section__Key)
```

- En **local** (`dotnet watch / run`): `ASPNETCORE_ENVIRONMENT=Development` → lee `appsettings.Development.json`
- En **Render**: `ASPNETCORE_ENVIRONMENT=Production` → lee solo vars de entorno del dashboard

---

## 2. Setup inicial

### Prerrequisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22+](https://nodejs.org)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)
- [dotnet-ef tools](https://learn.microsoft.com/en-us/ef/core/cli/dotnet)

```bash
# Instalar dotnet-ef globalmente (una sola vez)
dotnet tool install --global dotnet-ef

# Verificar
dotnet ef --version
```

### Primera vez en el repo

```bash
# 1. Clonar
git clone <repo-url>
cd ContableAI

# 2. Instalar dependencias frontend
cd frontend && npm ci && cd ..

# 3. Restaurar paquetes backend
cd backend && dotnet restore && cd ..

# 4. Levantar PostgreSQL local
docker compose up -d

# 5. Aplicar migraciones y seed (corre automático al iniciar la API)
# Ver sección "Levantar el proyecto" abajo
```

---

## 3. Levantar el proyecto localmente

### PostgreSQL (Docker)

```bash
# Levantar en background
docker compose up -d

# Ver estado
docker compose ps

# Ver logs de PostgreSQL
docker compose logs -f postgres

# Detener (conserva los datos)
docker compose down

# Detener y borrar datos (DB limpia)
docker compose down -v
```

Credenciales locales:
- Host: `localhost:5432`
- DB: `contableai_dev`
- Usuario: `postgres`
- Password: `CHANGE_ME`

### API .NET (con hot reload)

```bash
cd backend
dotnet watch --project src/ContableAI.API
```

Al iniciar, la API aplica las migraciones pendientes y ejecuta el seed automáticamente.

URLs disponibles:
- API: `http://localhost:5284`
- Documentación (Scalar): `http://localhost:5284/scalar/v1`
- Health check (liveness): `http://localhost:5284/health/live`
- Health check (readiness, incluye PostgreSQL): `http://localhost:5284/health/ready`
- Hangfire: `http://localhost:5284/hangfire`

### Frontend Angular

```bash
cd frontend
npx ng serve
# http://localhost:4200
```

El frontend apunta a `http://localhost:5284/api` en modo development (definido en `src/environments/environment.ts`).

---

## 4. Backend .NET

Todos los comandos desde `backend/`.

```bash
# Restaurar paquetes
dotnet restore

# Compilar (dev)
dotnet build

# Compilar (Release — igual que CI)
dotnet build --configuration Release

# Ejecutar sin hot reload
dotnet run --project src/ContableAI.API

# Ejecutar con hot reload (recomendado para desarrollo)
dotnet watch --project src/ContableAI.API

# Verificar vulnerabilidades en paquetes
dotnet list src/ContableAI.API/ContableAI.API.csproj package --vulnerable --include-transitive
dotnet list src/ContableAI.Infrastructure/ContableAI.Infrastructure.csproj package --vulnerable --include-transitive
```

---

## 5. Frontend Angular

Todos los comandos desde `frontend/`.

```bash
# Instalar dependencias (usar ci en lugar de install para reproducibilidad)
npm ci

# Servidor de desarrollo con hot reload
npx ng serve
# http://localhost:4200

# Build de producción
npx ng build --configuration production
# Artefactos en: dist/frontend/browser/

# Build de desarrollo
npx ng build --configuration development

# Verificar errores TypeScript sin compilar
npx tsc --noEmit

# Analizar tamaño del bundle
npx ng build --configuration production --stats-json
npx webpack-bundle-analyzer dist/frontend/browser/stats.json
```

### Cambiar la URL del API para apuntar a producción (temporal)

Editar `src/environments/environment.production.ts`:
```typescript
export const environment = {
  production: true,
  apiUrl: 'https://contableai-api.onrender.com/api'
};
```

---

## 6. Migraciones EF Core

Todos los comandos desde `backend/`, siempre con los flags `--project` y `--startup-project`.

```bash
# Ver estado de migraciones aplicadas
dotnet ef migrations list \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API

# Crear una nueva migración
dotnet ef migrations add <NombreMigracion> \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
# Usar PascalCase descriptivo: AddPasswordResetToken, AddClosedPeriods, etc.

# Aplicar migraciones pendientes (local)
dotnet ef database update \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API

# Revertir a una migración específica
dotnet ef database update <NombreMigracionDestino> \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API

# Eliminar la última migración (solo si NO fue aplicada a la DB)
dotnet ef migrations remove \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API

# Generar script SQL idempotente para producción
dotnet ef migrations script \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API \
  --idempotent \
  --output migrations.sql
```

### Aplicar migraciones en Neon (producción) desde CLI

En producción las migraciones se aplican solas al iniciar la API en Render.
Para forzarlas desde CLI sin deployar:

```bash
cd backend
ASPNETCORE_ENVIRONMENT=Production \
ConnectionStrings__DefaultConnection='<neon-string-sin-pooler>' \
Jwt__Key='<jwt-key>' \
Jwt__Issuer='ContableAI' \
Jwt__Audience='ContableAI' \
dotnet ef database update \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```

> Usar el endpoint **sin** `-pooler` para operaciones DDL: `ep-wandering-bread-a8wmn9bc.eastus2.azure.neon.tech`

---

## 7. Reset de base de datos

### Reset local (Docker)

```bash
cd backend
ASPNETCORE_ENVIRONMENT=Development dotnet ef database drop --force \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API

ASPNETCORE_ENVIRONMENT=Development dotnet ef database update \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```

El seed (reglas globales + plan de cuentas) corre automáticamente al iniciar la API.

### Reset Neon (producción) — DESTRUCTIVO

```bash
cd backend

# Drop
ASPNETCORE_ENVIRONMENT=Production \
ConnectionStrings__DefaultConnection='Host=ep-wandering-bread-a8wmn9bc.eastus2.azure.neon.tech; Database=neondb; Username=neondb_owner; Password=<pwd>; SSL Mode=VerifyFull;' \
Jwt__Key='<jwt-key>' Jwt__Issuer='ContableAI' Jwt__Audience='ContableAI' \
dotnet ef database drop --force \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API

# Recrear
ASPNETCORE_ENVIRONMENT=Production \
ConnectionStrings__DefaultConnection='Host=ep-wandering-bread-a8wmn9bc.eastus2.azure.neon.tech; Database=neondb; Username=neondb_owner; Password=<pwd>; SSL Mode=VerifyFull;' \
Jwt__Key='<jwt-key>' Jwt__Issuer='ContableAI' Jwt__Audience='ContableAI' \
dotnet ef database update \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```

> El seed en Neon corre en el próximo startup de la API en Render.

---

## 8. Variables de entorno y secretos

### Archivos de configuración

```
backend/src/ContableAI.API/
├── appsettings.json              ← base limpia sin secretos (commiteado)
├── appsettings.Development.json  ← overrides locales Docker (gitignoreado)
└── appsettings.Production.json   ← NO existe en repo (usar env vars en Render)
```

### Variables requeridas en producción (Render dashboard)

| Variable de entorno | Descripción | Ejemplo |
|---------------------|-------------|---------|
| `ASPNETCORE_ENVIRONMENT` | Modo de ejecución | `Production` |
| `ConnectionStrings__DefaultConnection` | Connection string Neon (con pooler) | `Host=ep-...pooler...; Database=neondb; ...` |
| `Jwt__Key` | Clave JWT (mínimo 32 chars) | *(ver .env local)* |
| `Jwt__Issuer` | Issuer del token | `ContableAI` |
| `Jwt__Audience` | Audience del token | `ContableAI` |
| `Smtp__Password` | API key de Resend | `re_...` |
| `Frontend__BaseUrl` | URL del frontend para CORS | `https://contable-ai-sandy.vercel.app` |

### Generar una JWT Key segura

```bash
# PowerShell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])

# bash / Linux / macOS
openssl rand -base64 48
```

### Referencia de secretos locales (archivo .env — NO commiteado)

El archivo `.env` en la raíz del repo tiene las credenciales de referencia para uso personal.
Las claves están en el formato:
```
resend=<api-key>
neon="<connection-string>"
render=<url-api>
front=<url-frontend>
```

---

## 9. Tests

Todos los comandos desde `backend/`.

```bash
# Correr todos los tests
dotnet test

# Con output detallado
dotnet test --verbosity normal

# En Release (igual que CI)
dotnet test --configuration Release --verbosity normal

# Filtrar por clase
dotnet test --filter "FullyQualifiedName~BankTransactionTests"

# Con reporte de cobertura
dotnet test --collect:"XPlat Code Coverage"
# Resultados en: tests/ContableAI.Tests/TestResults/

# Ver cobertura en HTML (requiere reportgenerator)
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator \
  -reports:"tests/ContableAI.Tests/TestResults/**/coverage.cobertura.xml" \
  -targetdir:"coverage-report" \
  -reporttypes:Html

# Watch mode
dotnet watch test --project tests/ContableAI.Tests
```

### Tests frontend

```bash
cd frontend
npx ng test

# Headless (para CI)
npx ng test --no-watch --browsers ChromeHeadless
```

---

## 10. Deploy

### Backend → Render

El deploy en Render se activa automáticamente al hacer `push` a `main`.

Render buildea la imagen Docker con el `backend/Dockerfile` y expone el puerto `8080`.

**Para forzar un redeploy manual:** Ir al dashboard de Render → Manual Deploy.

**Al iniciar, la API:**
1. Lee env vars del dashboard de Render
2. Aplica migraciones pendientes (`MigrateAsync`)
3. Ejecuta el seed (upsert — nunca borra datos existentes)

### Frontend → Vercel

El deploy en Vercel se activa automáticamente al hacer `push` a `main`.

Vercel buildea con `ng build --configuration production` y sirve el output.

El archivo `frontend/vercel.json` redirige todas las rutas a `index.html` para el SPA routing.

### Chequeo post-deploy

```bash
# Health check de la API
curl https://contableai-api.onrender.com/health/live    # liveness (proceso vivo)
curl https://contableai-api.onrender.com/health/ready   # readiness (PostgreSQL alcanzable)

# Respuesta esperada
{"status":"Healthy"}
```

---

## 11. Flujo de trabajo día a día

### Iniciar sesión de desarrollo

```bash
# Terminal 0: levantar PostgreSQL
docker compose up -d

# Terminal 1: API con hot reload
cd backend
dotnet watch --project src/ContableAI.API

# Terminal 2: frontend
cd frontend
npx ng serve
```

### Hacer un commit

```bash
# Verificar que no hay errores
cd backend && dotnet build && dotnet test
cd frontend && npx tsc --noEmit

# Stagear y commitear
git add <archivos>
git commit -m "tipo: descripción breve"
```

Prefijos de commits: `feat`, `fix`, `refactor`, `docs`, `chore`, `test`

### Agregar un campo a la base de datos

```bash
# 1. Modificar la entidad en ContableAI.Domain/Entities/
# 2. Agregar la configuración en ContableAI.Infrastructure/Persistence/

cd backend
dotnet ef migrations add <NombreDescriptivo> \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API

# La migración se aplica automáticamente al reiniciar la API
dotnet watch --project src/ContableAI.API
```

### Pushear a producción

```bash
git push origin main
# Render y Vercel deployarán automáticamente
# Monitorear en: https://dashboard.render.com
```

---

## 12. Checklists

### Nuevo developer en el proyecto

- [ ] Instalar .NET 10 SDK, Node 22, Docker Desktop
- [ ] `dotnet tool install --global dotnet-ef`
- [ ] `cd frontend && npm ci`
- [ ] `cd backend && dotnet restore`
- [ ] `docker compose up -d`
- [ ] Verificar que `appsettings.Development.json` existe (gitignoreado — recrear si no está)
- [ ] `cd backend && dotnet watch --project src/ContableAI.API` — la API hace el migration + seed sola
- [ ] `cd frontend && npx ng serve`
- [ ] Abrir `http://localhost:4200` y verificar que carga

### Deploy a producción

- [ ] Tests pasando: `cd backend && dotnet test --configuration Release`
- [ ] Build frontend OK: `cd frontend && npx ng build --configuration production`
- [ ] Si hay migraciones nuevas: revisar el SQL generado (`--idempotent`)
- [ ] `git push origin main` → Render y Vercel deployarán solos
- [ ] Verificar `/health/ready` después del deploy (debe dar 200 con PostgreSQL alcanzable)
- [ ] Revisar logs en Render si algo falla

### Configurar Render por primera vez (env vars)

- [ ] `ASPNETCORE_ENVIRONMENT` = `Production`
- [ ] `ConnectionStrings__DefaultConnection` = *(connection string Neon con pooler)*
- [ ] `Jwt__Key` = *(key de 48+ chars — ver .env local)*
- [ ] `Jwt__Issuer` = `ContableAI`
- [ ] `Jwt__Audience` = `ContableAI`
- [ ] `Smtp__Password` = *(API key de Resend — ver .env local)*
- [ ] `Frontend__BaseUrl` = `https://contable-ai-sandy.vercel.app`
- [ ] En Render → Settings → Health Check Path: fijar `/health/ready` (antes `/healthz`)
- [ ] Hacer un redeploy manual y verificar `/health/live` y `/health/ready`
