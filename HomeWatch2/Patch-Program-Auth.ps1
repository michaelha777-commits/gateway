param([Parameter(Mandatory=$true)][string]$ProgramPath)
$ErrorActionPreference = 'Stop'
$content = [IO.File]::ReadAllText($ProgramPath)

if ($content -notmatch 'AddHomeWatchAuthentication\(') {
    $content = $content.Replace(
        'var builder = WebApplication.CreateBuilder(args);',
        "var builder = WebApplication.CreateBuilder(args);`r`nbuilder.Services.AddHomeWatchAuthentication();")
}

if ($content -notmatch 'UseHomeWatchAuthentication\(') {
    $content = $content.Replace(
        "AdultSafetyOverrides.Configure(app.Environment);`r`napp.UseCors();",
        "AdultSafetyOverrides.Configure(app.Environment);`r`napp.UseHomeWatchAuthentication();`r`napp.UseCors();")
    $content = $content.Replace(
        "AdultSafetyOverrides.Configure(app.Environment);`napp.UseCors();",
        "AdultSafetyOverrides.Configure(app.Environment);`napp.UseHomeWatchAuthentication();`napp.UseCors();")
}

if ($content -notmatch 'MapHomeWatchAuthentication\(') {
    $content = $content.Replace(
        'app.MapRuntimeSettingsEndpoints();',
        "app.MapHomeWatchAuthentication();`r`napp.MapRuntimeSettingsEndpoints();")
}

[IO.File]::WriteAllText($ProgramPath, $content, [Text.UTF8Encoding]::new($false))
