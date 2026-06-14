FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ProjectCal.slnx ./
COPY src/ProjectCal.Shared/ProjectCal.Shared.csproj src/ProjectCal.Shared/
COPY src/ProjectCal.Api/ProjectCal.Api.csproj src/ProjectCal.Api/
RUN dotnet restore src/ProjectCal.Api/ProjectCal.Api.csproj
COPY src/ProjectCal.Shared src/ProjectCal.Shared
COPY src/ProjectCal.Api src/ProjectCal.Api
RUN dotnet publish src/ProjectCal.Api/ProjectCal.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ProjectCal.Api.dll"]
