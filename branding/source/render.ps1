# Renders an HTML page to a transparent 1024x1024 PNG with headless Edge (DevTools protocol).
param([string]$Html, [string]$Out, [int]$Width = 1024, [int]$Height = 1024, [int]$Port = 9334)
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$prof = Join-Path $env:TEMP "hb-render-prof"; Remove-Item -Recurse -Force $prof -ErrorAction SilentlyContinue
$p = Start-Process $edge -ArgumentList "--headless=new","--disable-gpu","--remote-debugging-port=$Port","--user-data-dir=$prof","--force-device-scale-factor=1","about:blank" -PassThru
try {
  $ws = $null
  for ($i = 0; $i -lt 40 -and -not $ws; $i++) { Start-Sleep -Milliseconds 250; try { foreach ($t in (Invoke-RestMethod "http://127.0.0.1:$Port/json")) { if ($t.type -eq "page" -and -not $ws) { $ws = $t.webSocketDebuggerUrl } } } catch {} }
  $c = New-Object System.Net.WebSockets.ClientWebSocket; $c.ConnectAsync([Uri]$ws, [Threading.CancellationToken]::None).Wait()
  $script:id = 0
  function Send($m, $params) {
    $script:id++; $b = [Text.Encoding]::UTF8.GetBytes((@{ id = $script:id; method = $m; params = $params } | ConvertTo-Json -Depth 10 -Compress))
    $c.SendAsync([ArraySegment[byte]]$b, 'Text', $true, [Threading.CancellationToken]::None).Wait()
    while ($true) { $sb = New-Object Text.StringBuilder; $buf = New-Object byte[] 1048576
      do { $r = $c.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None).Result; [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count)) } while (-not $r.EndOfMessage)
      $o = $sb.ToString() | ConvertFrom-Json; if ($o.id -eq $script:id) { return $o } }
  }
  Send 'Emulation.setDeviceMetricsOverride' @{ width = $Width; height = $Height; deviceScaleFactor = 1; mobile = $false } | Out-Null
  Send 'Emulation.setDefaultBackgroundColorOverride' @{ color = @{ r = 0; g = 0; b = 0; a = 0 } } | Out-Null
  Send 'Page.navigate' @{ url = ([Uri](Resolve-Path $Html).Path).AbsoluteUri } | Out-Null
  Start-Sleep -Seconds 2
  $r = Send 'Page.captureScreenshot' @{ format = 'png'; clip = @{ x = 0; y = 0; width = $Width; height = $Height; scale = 1 } }
  [IO.File]::WriteAllBytes($Out, [Convert]::FromBase64String($r.result.data)); "saved $Out"
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Get-Process msedge -ErrorAction SilentlyContinue | ? { $_.CommandLine -like "*hb-render-prof*" } | Stop-Process -Force -ErrorAction SilentlyContinue }
