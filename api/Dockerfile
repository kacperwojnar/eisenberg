FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY data/ data/
COPY api/ api/
RUN dotnet publish api/Eisenberg.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "Eisenberg.Api.dll"]
