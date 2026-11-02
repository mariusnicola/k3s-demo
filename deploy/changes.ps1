# Tells the monitor about work Kubernetes cannot see. Each line is one JSON object in a file the
# monitor tails. Used by setup.ps1, and by any script that works on the cluster:
#   . deploy\changes.ps1
#   $operation = Start-Operation 'Move workload' 'move' @('Import images', 'Switch traffic')
#   Set-OperationStep $operation 'Import images'
#   Write-Change 'image import' 'k3s-node1' $null 'api:1234'
#   Stop-Operation $operation $true
$ChangesScript = $PSCommandPath
# State stays in the real deploy folder when this runs from a release under it.
$StateDir = if ((Split-Path -Leaf (Split-Path $PSScriptRoot)) -eq '.release') { Split-Path (Split-Path $PSScriptRoot) } else { $PSScriptRoot }
$ChangesDir = Join-Path $StateDir '.changes'
$ChangesFile = Join-Path $ChangesDir 'changes.jsonl'
$HeartbeatSeconds = 15

function Write-ChangeLine([hashtable]$Line) {
    if (-not (Test-Path $ChangesDir)) { New-Item -ItemType Directory -Path $ChangesDir | Out-Null }
    $Line['time'] = [DateTimeOffset]::UtcNow.ToString('o')
    $text = ($Line | ConvertTo-Json -Compress) + "`n"
    # The heartbeat writes from another process, so one write can find the file busy.
    foreach ($attempt in 1..5) {
        try {
            [IO.File]::AppendAllText($ChangesFile, $text)
            return
        } catch [IO.IOException] {
            Start-Sleep -Milliseconds 100
        }
    }
    Write-Warning "change not recorded: $text"
}

function Write-Change([string]$Kind, [string]$Object, [string]$Old, [string]$New) {
    Write-ChangeLine @{ type = 'change'; kind = $Kind; object = $Object; old = $Old; new = $New }
}

function Start-Operation([string]$Name, [string]$Work = $Name, [string[]]$Steps = @()) {
    $id = [Guid]::NewGuid().ToString('N')
    $line = @{ type = 'start'; id = $id; name = $Name; work = $Work }
    if ($Steps.Count -gt 0) { $line['steps'] = $Steps }
    Write-ChangeLine $line
    $marker = Join-Path $ChangesDir "$id.live"
    [IO.File]::WriteAllText($marker, '')
    # A process of its own, so a long blocking step keeps beating. It ends with this script, so a
    # killed run goes silent and the monitor reports that.
    $beat = @"
. '$ChangesScript'
while ((Test-Path '$marker') -and (Get-Process -Id $PID -ErrorAction SilentlyContinue)) {
    Write-ChangeLine @{ type = 'heartbeat'; id = '$id' }
    Start-Sleep -Seconds $HeartbeatSeconds
}
Remove-Item '$marker' -ErrorAction SilentlyContinue
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($beat))
    Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded
    return $id
}

function Set-OperationStep([string]$Id, [string]$Step) {
    Write-ChangeLine @{ type = 'step'; id = $Id; step = $Step }
}

function Stop-Operation([string]$Id, [bool]$Ok) {
    Write-ChangeLine @{ type = 'end'; id = $Id; ok = $Ok }
    Remove-Item (Join-Path $ChangesDir "$Id.live") -ErrorAction SilentlyContinue
}
