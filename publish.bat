@echo off
dotnet publish -c Release -f net9.0-android -r android-arm64 /p:AndroidPackageFormat=apk /p:PublishDir=bin\publish\