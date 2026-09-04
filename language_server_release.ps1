<#
.SYNOPSIS
    Compiles Roslyn SDK

.EXAMPLE
    .\langauge_server_release.ps1                                       # Compiles and saves to ./artifacts/LanguageServerRelease
    .\langauge_server_release.ps1 -Live C:\Users\aljaz\.vscode\extensions\ms-dotnettools.csharp-2.140.9-win32-x64   # Compiles, saves to ./artifacts/LanguageServerRelease and attempts to copy to live
#>
[CmdletBinding(DefaultParameterSetName = 'Patch')]
param(
    [Parameter(ParameterSetName = 'Live')][System.IO.DirectoryInfo]$live
)

$release = ".\artifacts\LanguageServerRelease"
$ok = $true

dotnet publish .\src\LanguageServer\Microsoft.CodeAnalysis.LanguageServer\ -c Release -o .\artifacts\LanguageServerRelease /p:DebugType=portable /p:IncludeSymbols=true
if ($LASTEXITCODE -ne 0) {
    $ok = $false
}

if ($ok) {
    if($live) {
        $live = Join-Path $live ".roslyn"
        Copy-Item -Path "$release\*" -Destination $live -Recurse -Force
    }
}