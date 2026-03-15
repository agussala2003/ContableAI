# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first to leverage Docker layer caching
COPY ["ContableAI.slnx", "./"]
COPY ["src/ContableAI.API/ContableAI.API.csproj", "src/ContableAI.API/"]
COPY ["src/ContableAI.Application/ContableAI.Application.csproj", "src/ContableAI.Application/"]
COPY ["src/ContableAI.Domain/ContableAI.Domain.csproj", "src/ContableAI.Domain/"]
COPY ["src/ContableAI.Infrastructure/ContableAI.Infrastructure.csproj", "src/ContableAI.Infrastructure/"]

RUN dotnet restore "ContableAI.slnx"

# Copy the rest of the source code and publish
COPY . .
RUN dotnet publish "src/ContableAI.API/ContableAI.API.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "ContableAI.API.dll"]
