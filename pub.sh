#!/usr/bin/env bash

dotnet pack ./src/Dtr.Blazor.HotReload/Dtr.Blazor.HotReload.csproj
dotnet pack ./src/Dtr.Blazor.HotReload.Desktop/Dtr.Blazor.HotReload.Desktop.csproj
dotnet pack ./src/Dtr.Blazor.HotReload.Server/Dtr.Blazor.HotReload.Server.csproj
