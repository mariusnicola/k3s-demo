$wslConfig = Get-Content "$env:USERPROFILE\.wslconfig"

Test-Check 'WSL distro running' {
    $state = (wsl -l -v | Out-String) -replace "`0", ''
    Assert-True ($state -match "$Distro\s+Running\s+2") 'distro not running on WSL 2'
}

Test-Check 'WSL idle shutdown off' {
    Assert-True ($wslConfig -contains 'instanceIdleTimeout=-1') 'instanceIdleTimeout=-1 missing'
}

Test-Check 'WSL starts at logon' {
    $task = schtasks /query /tn 'BatchTracker-wsl-start' /v /fo csv 2>$null | ConvertFrom-Csv
    Assert-True ($null -ne $task) 'task missing'
    Assert-True ($task.'Scheduled Task State' -eq 'Enabled') 'task disabled'
    Assert-True ($task.'Task To Run' -match "wsl\.exe -d $Distro") "runs $($task.'Task To Run')"
    Assert-True ($task.'Last Result' -eq '0') "last result $($task.'Last Result')"
}

Test-Check 'WSL caps unchanged' {
    Assert-True (($wslConfig -contains 'memory=16GB') -and ($wslConfig -contains 'processors=12')) 'caps differ from memory=16GB, processors=12'
}
