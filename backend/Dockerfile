# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first to leverage Docker layer caching
COPY ["ContableAI.slnx", "./"]
COPY ["src/ContableAI.API/ContableAI.API.csproj", "src/ContableAI.API/"]
COPY ["src/ContableAI.Application/ContableAI.Application.csproj", "src/ContableAI.Application/"]
COPY ["src/ContableAI.Domain/ContableAI.Domain.csproj", "src/ContableAI.Domain/"]
COPY ["src/ContableAI.Infrastructure/ContableAI.Infrastructure.csproj", "src/ContableAI.Infrastructure/"]

RUN dotnet restore "src/ContableAI.API/ContableAI.API.csproj"

# Copy the rest of the source code and publish
COPY . .
RUN dotnet publish "src/ContableAI.API/ContableAI.API.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Tesseract OCR + español para procesar PDFs escaneados
# libfontconfig1: necesario para PDFium (renderizado de páginas PDF)
RUN apt-get update && apt-get install -y --no-install-recommends \
    tesseract-ocr \
    tesseract-ocr-spa \
    libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# El paquete NuGet Tesseract 5.2.0 solo trae binarios de Windows. En Linux busca, dentro de
# /app/x64, "libleptonica-1.82.0.so" y "libtesseract50.so", y carga "libdl" sin versión. apt
# instala liblept.so.5, libtesseract.so.5 y libdl.so.2: sin estos symlinks el OCR falla con
# DllNotFoundException ("Exception has been thrown by the target of an invocation").
RUN LIBDIR="$(dirname "$(find /usr/lib -name liblept.so.5 | head -1)")" \
    && mkdir -p /app/x64 \
    && ln -s "$LIBDIR/liblept.so.5"      /app/x64/libleptonica-1.82.0.so \
    && ln -s "$LIBDIR/libtesseract.so.5" /app/x64/libtesseract50.so \
    && ln -sf "$(find /usr/lib /lib -name libdl.so.2 | head -1)" "$LIBDIR/libdl.so"

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Apunta al tessdata instalado por apt (spa + eng) en Ubuntu 24.04
ENV TESSDATA_PREFIX=/usr/share/tesseract-ocr/5/tessdata
# Tesseract paraleliza con OpenMP según los cores que VE el contenedor (16+), no según la cuota de
# CPU real. Con la cuota chica de Render los threads se pisan: medido con 0.1 CPU, un extracto de
# 4 páginas no termina en 7 minutos sin esto y tarda ~50 s con un solo thread.
ENV OMP_THREAD_LIMIT=1
# Tesseract y PDFium reservan memoria nativa con malloc; glibc abre un arena por thread y cada job
# de Hangfire corre en uno distinto, así que el RSS crece entre OCRs sin volver al sistema. Con 2
# arenas la memoria liberada se reutiliza y la instancia de 512 MB no llega al OOM.
ENV MALLOC_ARENA_MAX=2

ENTRYPOINT ["dotnet", "ContableAI.API.dll"]
