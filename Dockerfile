FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY BackInStock/BackInStock.csproj BackInStock/
RUN dotnet restore BackInStock/BackInStock.csproj
COPY BackInStock/ BackInStock/
RUN dotnet publish BackInStock/BackInStock.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV PORT=8080 DB_PATH=/data/backinstock.db
VOLUME /data
EXPOSE 8080
CMD ["dotnet", "BackInStock.dll"]
