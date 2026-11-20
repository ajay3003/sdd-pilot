# Starts (or restarts) the local stand-in website for the Frontend Quality Review browser test: node:20-bookworm from the local image store
# (no pull), server.js mounted read-only, published on all interfaces (the ZAP container reaches host-published ports only when they are not
# bound to loopback). Prints the URL to use as BIRKNEXT_FQR_TARGET_URL.
param([int]$Port = 18092, [string]$Name = 'birknext-fqr-standin')
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
podman rm --force --ignore $Name | Out-Null
podman run --detach --rm --pull=never --name $Name --label birknext.managed=true --label birknext.component=frontend-quality-standin `
    -p "${Port}:8080" -v "${here}\server.js:/app/server.js:ro" docker.io/library/node:20-bookworm node /app/server.js | Out-Null
for ($i = 0; $i -lt 30; $i++) {
    # curl.exe, not Invoke-WebRequest: no system proxy and no IE engine in the way of a loopback check.
    $status = & curl.exe -s -o NUL -w "%{http_code}" --max-time 2 "http://localhost:$Port/__control"
    if ($status -eq '200') { "http://localhost:$Port"; exit 0 }
    Start-Sleep -Milliseconds 500
}
Write-Error "The frontend stand-in did not answer on port $Port."; exit 1
