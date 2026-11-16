FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY data/ data/
COPY worker/ worker/
RUN dotnet publish worker/Eisenberg.Worker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5001
EXPOSE 5001
ENTRYPOINT ["dotnet", "Eisenberg.Worker.dll"]
