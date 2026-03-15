# ContableAI — Referencia de Comandos

> Todos los comandos asumen que estás parado en la raíz del repo (`ContableAI/`)  
> a menos que se indique lo contrario.

---

## Índice

1. [Infraestructura local (Docker)](#1-infraestructura-local-docker)
2. [Backend .NET](#2-backend-net)
3. [Migraciones EF Core](#3-migraciones-ef-core)
4. [Tests](#4-tests)
5. [Frontend Angular](#5-frontend-angular)
6. [Secretos y variables de entorno](#6-secretos-y-variables-de-entorno)
7. [CI / CD — GitHub Actions](#7-ci--cd--github-actions)
8. [Flujo de trabajo recomendado (día a día)](#8-flujo-de-trabajo-recomendado-día-a-día)
9. [Checklists de deploy](#9-checklists-de-deploy)

---

## 1. Infraestructura local (Docker)

### Levantar servicios de soporte (SQL Server + Qdrant)
```bash
docker compose up -d
```

### Ver estado de los contenedores
```bash
docker compose ps
```

### Bajar los contenedores (mantiene los volúmenes)
```bash
docker compose down
```

### Bajar y borrar los volúmenes (DB limpia — perderás los datos)
```bash
docker compose down -v
```

### Ver logs de SQL Server en tiempo real
```bash
docker compose logs -f sqlserver
```

### Conectarse a SQL Server desde la terminal
```bash
docker exec -it contableai-sqlserver-1 /opt/mssql-tools/bin/sqlcmd \
  -S localhost -U sa -P "YourStrongPass123!"
```

### Qdrant Web UI
```
http://localhost:6333/dashboard
```

---

## 2. Backend .NET

Todos los comandos desde `backend/`.

### Restaurar paquetes
```bash
dotnet restore
```

### Compilar (modo desarrollo)
```bash
dotnet build
```

### Compilar (modo Release — igual que el CI)
```bash
dotnet build --configuration Release
```

### Ejecutar la API
```bash
dotnet run --project src/ContableAI.API
# Escucha en: http://localhost:5284
# Swagger/Scalar: http://localhost:5284/scalar/v1
# Health check:  http://localhost:5284/healthz
```

### Ejecutar con hot reload
```bash
dotnet watch --project src/ContableAI.API
```

### Verificar vulnerabilidades de packages
```bash
dotnet list src/ContableAI.API/ContableAI.API.csproj package --vulnerable --include-transitive
dotnet list src/ContableAI.Infrastructure/ContableAI.Infrastructure.csproj package --vulnerable --include-transitive
```

### Actualizar un package
```bash
dotnet add src/ContableAI.API/ContableAI.API.csproj package <NombrePackage> --version <x.y.z>
```

---

## 3. Migraciones EF Core

Todos los comandos desde `backend/` con `--project src/ContableAI.Infrastructure --startup-project src/ContableAI.API`.

### Ver estado de migraciones
```bash
dotnet ef migrations list \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```

### Crear una nueva migración
```bash
dotnet ef migrations add <NombreMigracion> \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```
> Usar nombres descriptivos en PascalCase, ej: `AddPasswordResetToken`, `AddPeriodClosing`

### Aplicar migraciones pendientes a la DB
```bash
dotnet ef database update \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```
> En producción esto lo hace el startup automáticamente (`await app.SeedDatabaseAsync()`).

### Revertir a una migración anterior
```bash
dotnet ef database update <NombreMigracionDestino> \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```

### Eliminar la última migración (si aún no fue aplicada)
```bash
dotnet ef migrations remove \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```

### Generar script SQL para deploy en producción
```bash
dotnet ef migrations script \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API \
  --idempotent \
  --output migrations.sql
```
> `--idempotent` genera un script que se puede ejecutar varias veces sin error.

---

## 4. Tests

Todos los comandos desde `backend/`.

### Correr todos los tests
```bash
dotnet test
```

### Correr tests con output detallado
```bash
dotnet test --verbosity normal
```

### Correr tests en Release (igual que CI)
```bash
dotnet test --configuration Release --verbosity normal
```

### Correr solo una clase de tests
```bash
dotnet test --filter "FullyQualifiedName~BankTransactionTests"
```

### Correr tests con reporte de cobertura
```bash
dotnet test --collect:"XPlat Code Coverage"
# Los resultados quedan en tests/ContableAI.Tests/TestResults/
```

### Ver cobertura en consola (requiere reportgenerator)
```bash
# Instalar la tool globalmente (una sola vez)
dotnet tool install -g dotnet-reportgenerator-globaltool

# Generar reporte HTML
reportgenerator \
  -reports:"tests/ContableAI.Tests/TestResults/**/coverage.cobertura.xml" \
  -targetdir:"coverage-report" \
  -reporttypes:Html
```

### Correr tests en watch mode
```bash
dotnet watch test --project tests/ContableAI.Tests
```

---

## 5. Frontend Angular

Todos los comandos desde `frontend/`.

### Instalar dependencias
```bash
npm ci
```

### Servidor de desarrollo
```bash
npx ng serve
# http://localhost:4200
```

### Build de desarrollo
```bash
npx ng build --configuration development
```

### Build de producción
```bash
npx ng build --configuration production
# Artefactos en dist/frontend/browser/
```

### Correr tests unitarios
```bash
npx ng test
```

### Correr tests sin browser (headless — para CI)
```bash
npx ng test --no-watch --browsers ChromeHeadless
```

### Verificar que no hay errores TypeScript sin compilar
```bash
npx tsc --noEmit
```

### Analizar el bundle (tamaño de chunks)
```bash
npx ng build --configuration production --stats-json
npx webpack-bundle-analyzer dist/frontend/browser/stats.json
```

### Actualizar Angular
```bash
npx ng update @angular/core @angular/cli
```

---

## 6. Secretos y variables de entorno

### Estructura de archivos de configuración
```
backend/src/ContableAI.API/
├── appsettings.json              ← valores por defecto (commiteado, sin secretos)
├── appsettings.Development.json  ← overrides locales (commiteado, sin secretos reales)
└── appsettings.Production.json   ← NO commiteado (.gitignore lo excluye)
```

### Variables que DEBEN configurarse en producción

| Clave | Descripción |
|-------|------------|
| `ConnectionStrings:DefaultConnection` | Cadena de conexión a SQL Server de producción |
| `Jwt:Key` | Mínimo 32 caracteres, generado aleatoriamente |
| `OpenAI:ApiKey` | API key de OpenAI |
| `Afip:Cuit` | CUIT del estudio contable |
| `Afip:CertificatePath` | Ruta al `.p12` (en `backend/certs/`) |
| `Afip:CertificatePassword` | Password del certificado digital AFIP |
| `MercadoPago:AccessToken` | Access token de producción de MercadoPago |

### Generar una JWT Key segura
```bash
# PowerShell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])

# bash / Linux
openssl rand -base64 48
```

### .NET User Secrets (para desarrollo local, sin archivos)
```bash
# Desde backend/src/ContableAI.API/
dotnet user-secrets set "Jwt:Key" "mi-clave-local-secreta-de-mas-de-32-chars"
dotnet user-secrets set "OpenAI:ApiKey" "sk-proj-..."
dotnet user-secrets list
```

---

## 7. CI / CD — GitHub Actions

El workflow está en `.github/workflows/ci.yml`.

### Se dispara automáticamente en:
- `push` a `main` o `develop`
- `pull_request` hacia `main`

### Jobs
| Job | Pasos |
|-----|-------|
| **backend** | checkout → setup .NET 10 → `dotnet restore` → `dotnet build --Release` → `dotnet test --Release` → upload `.trx` |
| **frontend** | checkout → setup Node 22 → `npm ci` → `ng build --development` → `ng build --production` |

### Ejecutar el CI localmente con `act` (opcional)
```bash
# Instalar act: https://github.com/nektos/act
act push --job backend
act push --job frontend
```

### Agregar secretos al repositorio en GitHub
```
GitHub repo → Settings → Secrets and variables → Actions → New repository secret
```
Secretos recomendados para el CD futuro:
- `PROD_CONNECTION_STRING`
- `PROD_JWT_KEY`
- `PROD_OPENAI_API_KEY`
- `PROD_AFIP_CERT_PASSWORD`

---

## 8. Flujo de trabajo recomendado (día a día)

### Iniciar sesión de desarrollo
```bash
# 1. Levantar infraestructura
docker compose up -d

# 2. Terminal 1 — API con hot reload
cd backend
dotnet watch --project src/ContableAI.API

# 3. Terminal 2 — Angular
cd frontend
npx ng serve

# 4. (Opcional) Si modificaste el schema
cd backend
dotnet ef migrations add <NombreCambio> \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
dotnet ef database update \
  --project src/ContableAI.Infrastructure \
  --startup-project src/ContableAI.API
```

### Antes de hacer un commit
```bash
# Backend
cd backend
dotnet build
dotnet test

# Frontend
cd frontend
npx ng build --configuration development
```

### Antes de mergear a main
```bash
# Backend — build Release + tests
cd backend
dotnet build --configuration Release
dotnet test --configuration Release --verbosity normal

# Frontend — build producción
cd frontend
npx ng build --configuration production

# Verificar vulnerabilidades
cd backend
dotnet list src/ContableAI.API/ContableAI.API.csproj package --vulnerable --include-transitive
```

---

## 9. Checklists de deploy

### Nuevo entorno de producción
- [ ] Crear `appsettings.Production.json` con todos los secretos reales
- [ ] Generar JWT Key con `openssl rand -base64 48`
- [ ] Copiar certificado AFIP a `backend/certs/afip.p12`
- [ ] Levantar SQL Server (o apuntar a uno existente)
- [ ] Levantar Qdrant (o configurar un hosted)
- [ ] `dotnet publish --configuration Release --output ./publish`
- [ ] Aplicar migraciones: la API las aplica sola en startup
- [ ] Configurar HTTPS / certificado TLS
- [ ] Verificar `/healthz` retorna `{ "status": "Healthy" }`
- [ ] Cambiar `CORS` origin en `ServiceExtensions.cs` de `localhost:4200` a dominio real

### Actualización de producción
- [ ] Correr tests localmente (`dotnet test --configuration Release`)
- [ ] Verificar que no hay CVEs nuevos (`--vulnerable`)
- [ ] Si hay migraciones: revisar el diff del SQL generado (`--idempotent`)
- [ ] Deploy (la API aplica migraciones sola)
- [ ] Verificar `/healthz` después del deploy
- [ ] Revisar logs: `backend/logs/contableai-<fecha>.log`
