$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $output = Join-Path $repo 'artifacts/AutoScript'
    $packages = Join-Path $repo 'artifacts/packages'
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    dotnet publish AutoScript/AutoScript.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    # Official Python NuGet distribution, pinned; NuGet retains its signature verification.
    dotnet restore packaging/PythonRuntime.csproj --packages $packages
    if ($LASTEXITCODE -ne 0) { throw 'Python runtime restore failed' }
    $pythonSource = Join-Path $packages 'python/3.12.10/tools'
    $pythonDest = Join-Path $output 'Resources/python'
    New-Item -ItemType Directory -Force $pythonDest | Out-Null
    Copy-Item "$pythonSource/*" $pythonDest -Recurse -Force
    Copy-Item README.md $output
    @'
AutoScript - Windows x64, full portable package
Extract the entire ZIP, then open AutoScript.exe.
.NET Desktop Runtime and Python are included. No separate installation required.
Enter your Gemini API key and load models. Internet and Gemini quota are required.
Keep Resources alongside AutoScript.exe.
See README.md for the workflow and data storage details.
'@ | Set-Content (Join-Path $output 'START-HERE.txt') -Encoding utf8
    # Exercise the same bundled interpreter used by the application, on Windows.
    $probe = Join-Path $repo 'artifacts/python-probe.txt'
    [IO.File]::WriteAllText($probe, 'I held the key.', [Text.UTF8Encoding]::new($false))
    $result = & "$pythonDest/python.exe" -I "$output/Resources/validate_script.py" $probe
    if ($LASTEXITCODE -ne 0) { throw 'Bundled Python failed' }
    $counts = $result | ConvertFrom-Json
    if ($counts.words -ne 4 -or $counts.errors.Count -ne 0) { throw 'Bundled Python validation failed' }
    # Run full workflow checks with only the packaged interpreter selected.
    dotnet build tests/CoreChecks/CoreChecks.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'CoreChecks build failed' }
    $checks = Join-Path $repo 'tests/CoreChecks/bin/Release/net8.0'
    $checkPython = Join-Path $checks 'Resources/python'
    New-Item -ItemType Directory -Force $checkPython | Out-Null
    Copy-Item "$pythonDest/*" $checkPython -Recurse -Force
    dotnet "$checks/CoreChecks.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Bundled Python workflow checks failed' }
    # Window smoke test without a key or any live Gemini request.
    $app = Start-Process "$output/AutoScript.exe" -PassThru
    try {
        Start-Sleep -Seconds 8
        $app.Refresh()
        if ($app.HasExited -or $app.MainWindowHandle -eq 0) { throw 'WPF window did not start' }
    } finally { if (-not $app.HasExited) { Stop-Process -Id $app.Id } }
    $zip = Join-Path $repo 'artifacts/AutoScript-win-x64-full.zip'
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$output/*" -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  AutoScript-win-x64-full.zip" | Set-Content "$zip.sha256" -Encoding ascii
} finally { Pop-Location }
