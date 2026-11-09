FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["BankFlow.Api/BankFlow.Api.csproj", "BankFlow.Api/"]
COPY ["BankFlow.Application/BankFlow.Application.csproj", "BankFlow.Application/"]
COPY ["BankFlow.Domain/BankFlow.Domain.csproj", "BankFlow.Domain/"]
COPY ["BankFlow.Infrastructure/BankFlow.Infrastructure.csproj", "BankFlow.Infrastructure/"]
RUN dotnet restore "BankFlow.Api/BankFlow.Api.csproj"
COPY . .
WORKDIR "/src/BankFlow.Api"
RUN dotnet publish "BankFlow.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BankFlow.Api.dll"]
