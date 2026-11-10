# syntax=docker/dockerfile:1

# ---- Build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, using only project files, so Docker can cache the restore layer between code changes.
COPY global.json Directory.Build.props ./
COPY src/VendorFunding.Domain/VendorFunding.Domain.csproj src/VendorFunding.Domain/
COPY src/VendorFunding.Infrastructure/VendorFunding.Infrastructure.csproj src/VendorFunding.Infrastructure/
COPY src/VendorFunding.Api/VendorFunding.Api.csproj src/VendorFunding.Api/
RUN dotnet restore src/VendorFunding.Api/VendorFunding.Api.csproj

COPY src/ src/
RUN dotnet publish src/VendorFunding.Api/VendorFunding.Api.csproj \
    --configuration Release --no-restore --output /app

# ---- Runtime ----
# Chiseled images have no shell or package manager and run as a non-root user.
# The "-extra" variant includes ICU and time zone data, which SqlClient needs.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "VendorFunding.Api.dll"]
