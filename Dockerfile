FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Wukna.csproj", "Directory.Build.props", "global.json", "./"]
RUN dotnet restore "Wukna.csproj"
COPY . .
RUN dotnet publish "Wukna.csproj" --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/App_Data/profile-images /app/App_Data/data-protection-keys /app/App_Data/chat-attachments \
    && chown -R app:app /app/App_Data \
    && chmod 700 /app/App_Data/data-protection-keys /app/App_Data/chat-attachments
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Wukna.dll"]
