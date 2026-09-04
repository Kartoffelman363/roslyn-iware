$release = ".\artifacts\CompilerRelease"
$bincore = Join-Path $release "bincore"

dotnet publish .\src\Compilers\Core\MSBuildTask\MSBuild\Microsoft.Build.Tasks.CodeAnalysis.csproj -c Release -o $release /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
dotnet publish .\src\Compilers\Server\VBCSCompiler\AnyCpu\VBCSCompiler.csproj -c Release -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
dotnet publish .\src\Compilers\CSharp\csc\AnyCpu\csc.csproj -c Release -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
dotnet publish .\src\Compilers\VisualBasic\vbc\AnyCpu\vbc.csproj -c Release -o $bincore /p:DebugType=portable /p:IncludeSymbols=true -f net10.0