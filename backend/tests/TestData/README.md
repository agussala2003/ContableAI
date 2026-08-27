# TestData — PDFs de prueba de los parsers

Esta carpeta contiene los **datos de prueba** (extractos bancarios y comprobantes VEP de
AFIP) que usan los tests de regresión de los parsers (`PdfBankParser`, `PdfAfipParserService`).

> ⚠️ **Los PDFs reales NO se versionan.** Contienen datos sensibles (CUITs, importes, nombres
> de cuenta). En el repo solo viven los marcadores `.gitkeep` de cada carpeta y este README.
> Cada desarrollador —y vos, para la CI— coloca sus propios PDFs **anonimizados** localmente.

## Cómo funciona

- El proyecto de tests (`ContableAI.Tests.csproj`) copia el contenido de esta carpeta al
  directorio de salida del build (`bin/.../TestData/`) con `CopyToOutputDirectory`.
- El helper [`TestData`](../ContableAI.Tests/TestData.cs) resuelve la carpeta así:
  1. Variable de entorno **`CONTABLEAI_TESTDATA`** si está definida (útil en CI o para apuntar
     a un dataset guardado en otra ruta).
  2. Si no, `TestData/` dentro del output del proyecto de tests.
- **Si un PDF requerido no está presente, el test se reporta como `Skipped`, nunca como
  `Passed`.** Esto reemplaza el viejo patrón `if (!File.Exists(...)) return;`, que hacía pasar
  los tests en verde sin ejecutar una sola aserción (falsos verdes).

## Cómo colocar los PDFs

1. Anonimizá los extractos/VEPs reales (reemplazá CUIT, razón social y números de cuenta por
   valores ficticios; **mantené intactos importes, fechas y descripciones**, que es lo que
   los parsers verifican).
2. Copiá cada archivo en la carpeta correspondiente respetando **exactamente** el nombre que
   esperan los tests (ver tabla abajo).
3. Corré `dotnet test` desde `backend/`. Los tests con datos presentes se ejecutan; el resto
   queda `Skipped`.

Alternativa sin copiar dentro del repo: definí la variable de entorno apuntando a tu dataset:

```bash
# PowerShell
$env:CONTABLEAI_TESTDATA = "D:\datasets\contableai-testdata"
# bash
export CONTABLEAI_TESTDATA=/home/vos/datasets/contableai-testdata
```

## Estructura esperada

```
TestData/
├── extractos/
│   ├── BBVA/                     ← BbvaNov2024ParseTest (BBVA TB 11.2024.pdf, 012025.pdf)
│   ├── GALICIA/                  ← GaliciaParseRegressionTests, CurrencyDetectionTests
│   ├── SANTANDER/                ← SantanderParserTests (los 11 resúmenes mensuales)
│   ├── GALICIA USD/              ← GaliciaUsdParseTests, CurrencyDetectionTests
│   ├── CREDICOOP/                ← CredicoopParseRegressionTests, CurrencyDetectionTests
│   ├── MERCADO PAGO/             ← CurrencyDetectionTests
│   ├── BANCO CIUDAD/             ← CurrencyDetectionTests
│   ├── FIX PREPROD/              ← BbvaFixPreProdParseTest (0925/1025/1125/1225.pdf)
│   └── BBVA FALLAS 15-7-2026/    ← BbvaSplitRowDescriptionTests, StatementYearDetectionTests
└── afip/                         ← AfipParserTests (VEPs sueltos en la raíz)
    ├── vep AFIP CONTABLE AI/     ← AfipParserTests (dataset de VEPs individuales)
    └── PDF consolidado VEP/      ← AfipParserTests (consulta VEP consolidada)
```

Los nombres de archivo concretos que cada test busca están en los `[InlineData(...)]` y las
llamadas `TestData.PathTo(...)` de cada clase de test bajo
[`ContableAI.Tests/Infrastructure/`](../ContableAI.Tests/Infrastructure/).
