# Infrastructure smoke tests. Exit 0 = all checks passed. New area = new check-*.ps1 file.
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'lib.ps1')

$wait = Get-FailureWait $FailureStamp (Get-Date)
if ($wait -gt 0) {
    Write-Output "[ERROR] the last run's refused credentials still count. Run again in $wait min."
    exit 1
}

dotnet publish $ProbeDir -c Release -o $ProbePublish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }

Get-ChildItem $PSScriptRoot -Filter 'check-*.ps1' | Sort-Object Name | ForEach-Object {
    Write-Output "--- $($_.BaseName)"
    . $_.FullName
}

Write-Output "--- passed $script:Passed, failed $script:Failed"
exit $(if ($script:Failed -eq 0) { 0 } else { 1 })
