dotnet publish .\src\Compilers\Server\VBCSCompiler\AnyCpu\VBCSCompiler.csproj -c Debug -o .\artifacts\CompilerDebug /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
dotnet publish .\src\Compilers\CSharp\csc\AnyCpu\csc.csproj -c Debug -o .\artifacts\CompilerDebug /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
dotnet publish .\src\Compilers\VisualBasic\vbc\AnyCpu\vbc.csproj -c Debug -o .\artifacts\CompilerDebug /p:DebugType=portable /p:IncludeSymbols=true -f net10.0
