param(
    [Parameter(Mandatory=$true)][string]$Tool,
    [string]$Arguments = '{}',
    [int]$TimeoutSec = 180
)
$ErrorActionPreference = 'Stop'
$uri = "http://localhost:29520"
$h = @{ "Content-Type" = "application/json"; "Accept" = "application/json, text/event-stream" }

$init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"dsh-agent","version":"1.0"}}}'
$r = Invoke-WebRequest -Uri $uri -Method Post -Headers $h -Body $init -TimeoutSec $TimeoutSec -UseBasicParsing
$sid = $r.Headers["Mcp-Session-Id"]
if ($sid) { $h["Mcp-Session-Id"] = $sid }

try { Invoke-WebRequest -Uri $uri -Method Post -Headers $h -Body '{"jsonrpc":"2.0","method":"notifications/initialized"}' -TimeoutSec $TimeoutSec -UseBasicParsing | Out-Null } catch {}

$argsObj = $Arguments | ConvertFrom-Json
$params = @{ name = $Tool; arguments = $argsObj }
$body = @{ jsonrpc = "2.0"; id = 2; method = "tools/call"; params = $params } | ConvertTo-Json -Depth 80 -Compress
$r2 = Invoke-WebRequest -Uri $uri -Method Post -Headers $h -Body $body -TimeoutSec $TimeoutSec -UseBasicParsing
$content = [string]$r2.Content

# 解析 SSE 的 data: 行
$dataJson = $null
foreach ($line in ($content -split "`n")) {
    $t = $line.Trim()
    if ($t.StartsWith("data:")) {
        $cand = $t.Substring(5).Trim()
        if ($cand.Length -gt 0 -and $cand.StartsWith("{")) { $dataJson = $cand }
    }
}
if (-not $dataJson) { $dataJson = $content }

$parsed = $dataJson | ConvertFrom-Json
$result = $parsed.result
if ($null -ne $result -and $null -ne $result.structuredContent) {
    ($result.structuredContent | ConvertTo-Json -Depth 80 -Compress)
} elseif ($null -ne $result -and $null -ne $result.content) {
    ($result.content | ConvertTo-Json -Depth 80 -Compress)
} else {
    ($parsed | ConvertTo-Json -Depth 80 -Compress)
}
