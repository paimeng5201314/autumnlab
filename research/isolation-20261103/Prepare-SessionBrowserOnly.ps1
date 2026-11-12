# Explicit preparation only, under a synthetic Medium token so new protected
# files have the current user as owner. This never executes the native trial.
# It is not real ordinary-user, browser, input, clipboard or OS acceptance.
$ErrorActionPreference = 'Stop'
if ($args.Count) { throw 'This owned preparation helper accepts no arguments.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$build = Join-Path $workspace 'artifacts/isolation-completion-20261030/session-browser-builds/probe-9b570d18e59c4b1095e0e2cd48fa3409'
if (-not (Test-Path -LiteralPath (Join-Path $build 'build-result.json'))) { throw 'The reviewed compiled probe is missing.' }
& (Join-Path $workspace 'scripts/Run-SessionBrowserPrototype.ps1') -Stage Prepare -BuildDirectory $build
