FROM mcr.microsoft.com/dotnet/runtime:5.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["CloudflareDynamicDns/CloudflareDynamicDns.csproj", "CloudflareDynamicDns/"]
RUN dotnet restore "CloudflareDynamicDns/CloudflareDynamicDns.csproj"
COPY . .
WORKDIR "/src/CloudflareDynamicDns"
RUN dotnet build "CloudflareDynamicDns.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CloudflareDynamicDns.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CloudflareDynamicDns.dll"]
