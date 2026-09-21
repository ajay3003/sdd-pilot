$proxyHeaders = @{ Origin = 'http://localhost:5173' }
$proxyBase = 'http://localhost:5000/api/local-https-proxy'
$proxyCurrent = Invoke-RestMethod -Uri "$proxyBase/runtime" -Headers $proxyHeaders
if ($proxyCurrent.sessionId) { throw 'Existing runtime found; control test will not replace it.' }
$proxyScope = @{ ProfileId='proxy-edge-control'; ContextFingerprint=('A' * 64); EnvironmentType='Development'; TargetUrl='https://example.com/'; ApprovedHosts=@() } | ConvertTo-Json
$proxyRunning = Invoke-RestMethod -Method Post -Uri "$proxyBase/start" -Headers $proxyHeaders -ContentType 'application/json' -Body $proxyScope
$proxySession = @{ SessionId=$proxyRunning.sessionId; ProfileId=$proxyRunning.profileId; ContextFingerprint=$proxyRunning.contextFingerprint } | ConvertTo-Json
try {
    $proxyBrowser = Invoke-RestMethod -Method Post -Uri "$proxyBase/launch-edge" -Headers $proxyHeaders -ContentType 'application/json' -Body $proxySession
    $proxyEdgeProcess = Get-Process -Id $proxyBrowser.edgeProcessId -ErrorAction Stop
    $proxyExited = $proxyEdgeProcess.WaitForExit(15000)
    [pscustomobject]@{ RuntimeId=$proxyRunning.runtimeId; Port=$proxyRunning.port; EdgePid=$proxyEdgeProcess.Id; ExitedWithoutBrowserRefresh=$proxyExited; ExitCode=if($proxyExited){$proxyEdgeProcess.ExitCode}else{$null} } | ConvertTo-Json
} finally {
    Invoke-RestMethod -Method Post -Uri "$proxyBase/stop" -Headers $proxyHeaders -ContentType 'application/json' -Body $proxySession | Select-Object RuntimeId,RuntimeStatus,StopReason
}
