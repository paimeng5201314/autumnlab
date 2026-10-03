[CmdletBinding()]
param([string]$ReportPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $ReportPath) { $ReportPath = Join-Path $projectRoot 'artifacts/reports/source-check.json' }
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Condition) {
    $checks.Add([ordered]@{name=$Name;status=$(if($Condition){'passed'}else{'failed'})})
    if (-not $Condition) { throw "Source check failed: $Name" }
}
$status='failed'
try {
    $files=@(foreach($directory in @('src','tests','scripts','build','docs','autumnos-spec','sdk','samples','tools','schemas')) {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $directory) -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    })
    foreach($file in $files) {
        if($file.Extension -eq '.json') { $null=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 100 }
        if($file.Extension -in @('.csproj','.props','.targets','.xaml','.manifest')) {
            $settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
            $reader=[Xml.XmlReader]::Create($file.FullName,$settings)
            try { while($reader.Read()) {} } finally { $reader.Dispose() }
        }
        if($file.Extension -eq '.ps1') {
            $parseErrors=$null; $tokens=$null
            $null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$parseErrors)
            Check "PowerShell syntax: $($file.Name)" ($parseErrors.Count -eq 0)
        }
    }
    Check 'All source JSON and XML parse' $true
    $manifestSchema=Get-Content -LiteralPath (Join-Path $projectRoot 'sdk/manifest.schema.json') -Raw | ConvertFrom-Json
    $releaseSchema=Get-Content -LiteralPath (Join-Path $projectRoot 'sdk/release.schema.json') -Raw | ConvertFrom-Json
    foreach($field in @('saveFormatVersion','minReadableSaveFormatVersion','maxReadableSaveFormatVersion')) {
        Check "Package save schema range $field" ($manifestSchema.properties.$field.minimum -eq 1 -and $manifestSchema.properties.$field.maximum -eq 1000000)
    }
    Check 'Release save schema agrees with package schema' ($releaseSchema.properties.saveFormatVersion.minimum -eq 1 -and $releaseSchema.properties.saveFormatVersion.maximum -eq $manifestSchema.properties.saveFormatVersion.maximum)
    $requirements=Get-Content -LiteralPath (Join-Path $projectRoot 'autumnos-spec/requirements.json') -Raw | ConvertFrom-Json
    $acceptance=Get-Content -LiteralPath (Join-Path $projectRoot 'autumnos-spec/acceptance-tests.json') -Raw | ConvertFrom-Json
    $originalRequirements = @(1..57 | ForEach-Object { 'R{0:D3}' -f $_ })
    $originalAcceptance = @(1..57 | ForEach-Object { 'A{0:D3}' -f $_ }) + @(1..8 | ForEach-Object { 'X{0:D2}' -f $_ })
    Check '57 original requirements retained' (@($originalRequirements | Where-Object { $_ -notin $requirements.requirements.id }).Count -eq 0)
    Check '65 original acceptance definitions retained' (@($originalAcceptance | Where-Object { $_ -notin $acceptance.tests.id }).Count -eq 0)
    Check 'Single-instance requirement and acceptance added without duplicate IDs' ($requirements.requirements.Count -eq 58 -and
        @($requirements.requirements.id | Select-Object -Unique).Count -eq 58 -and 'R058' -in $requirements.requirements.id -and
        $acceptance.tests.Count -eq 66 -and @($acceptance.tests.id | Select-Object -Unique).Count -eq 66 -and 'A058' -in $acceptance.tests.id)
    [xml]$brand=Get-Content -LiteralPath (Join-Path $projectRoot 'build/Brand.props') -Raw
    Check 'Central product and producer' ($brand.Project.PropertyGroup.AutumnProductName -eq 'Lab Chronicles AutumnOS' -and $brand.Project.PropertyGroup.AutumnProducerCredit -eq '制作人：派蒙')
    [xml]$shell=Get-Content -LiteralPath (Join-Path $projectRoot 'src/AutumnOS.Shell/AutumnOS.Shell.csproj') -Raw
    Check 'Real WinUI project' ($shell.Project.PropertyGroup.UseWinUI -eq 'true' -and $shell.Project.PropertyGroup.AssemblyName -eq 'AutumnOS.Client')
    [xml]$bootstrap=Get-Content -LiteralPath (Join-Path $projectRoot 'src/AutumnOS.Bootstrap/AutumnOS.Bootstrap.csproj') -Raw
    Check 'Stable AutumnOS executable entry' ($bootstrap.Project.PropertyGroup.AssemblyName -eq 'AutumnOS' -and $bootstrap.Project.PropertyGroup.PublishSingleFile -eq 'true')
    $content=@($shell.Project.ItemGroup.Content | ForEach-Object Include)
    Check 'Public configuration linked from specification' ($content -contains '../../autumnos-spec/config/logto.public.json')
    $config=Get-Content -LiteralPath (Join-Path $projectRoot 'autumnos-spec/config/logto.public.json') -Raw | ConvertFrom-Json
    Check 'Provided public values retained' ($config.Logto.ClientId -eq 'tjck5m8ohjkw272y42adv' -and $config.Logto.Authority -eq 'https://account.labchronicles.cn/oidc')
    Check 'PKCE public client contract' ($config.Logto.UsePkce -and $config.Logto.PkceMethod -eq 'S256' -and $config.Logto.ClientAuthenticationMethod -eq 'none')
    $baseline=Get-Content -LiteralPath (Join-Path $projectRoot 'autumnos-spec/docs/04-store-and-updates.md') -Raw
    foreach($literal in @('autumnos-app','pm','sq','.autumn','plus','meta','paimeng5201314/autumnlab')) { Check "Retained product contract: $literal" ($baseline.Contains($literal)) }
    $privateHeader='-----BEGIN ' + '(RSA |EC |OPENSSH )?PRIVATE KEY-----'
    $credentialPattern='gh[pousr]' + '_[A-Za-z0-9]{30,}'
    foreach($file in ($files | Where-Object Extension -in @('.cs','.json','.ps1','.props','.csproj','.xaml','.js','.ts','.cjs','.html'))) {
        $text=Get-Content -LiteralPath $file.FullName -Raw
        if($text -match $privateHeader -or $text -match $credentialPattern) { throw 'Potential credential material detected; no matching content is printed.' }
    }
    Check 'Narrow credential pattern scan' $true
    $status='passed'
} catch { $failure=$_.Exception.Message }
finally {
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) -Force | Out-Null
    [ordered]@{status=$status;checks=$checks;failure=$failure;executed_utc=[DateTimeOffset]::UtcNow.ToString('o');product_tests_executed=$false;
        meaning='Static source checks only. Credential patterns are not a complete security audit.'} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
Write-Host "Source checks: $status ($($checks.Count)); $ReportPath"
if($status -ne 'passed') { throw $failure }
