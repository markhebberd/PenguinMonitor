@echo off
dotnet publish -c Release -f net9.0-android /p:AndroidPackageFormat=apk /p:PublishDir=bin\publish\