dotnet pack -c Release
set /p version=Package version:
dotnet nuget push bin\Release\AgenticDotNet.%version%.nupkg --api-key %NUGET_AGENTIC_DOTNET_API_KEY% --source https://api.nuget.org/v3/index.json