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

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Apunta al tessdata instalado por apt (contiene spa.traineddata)
ENV TESSDATA_PREFIX=/usr/share/tessdata

ENTRYPOINT ["dotnet", "ContableAI.API.dll"]
