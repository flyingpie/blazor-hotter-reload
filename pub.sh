#!/usr/bin/env bash

dotnet pack ./src/BlazorHMR/BlazorHMR.csproj
dotnet pack ./src/BlazorHMR.Desktop/BlazorHMR.Desktop.csproj
dotnet pack ./src/BlazorHMR.Server/BlazorHMR.Server.csproj
