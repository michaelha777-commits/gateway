@echo off
setlocal
cd /d "%~dp0"

echo Applying persistent HomeWatch event cache...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$path = Join-Path (Get-Location) 'HomeWatch.ps1';" ^
  "if (-not (Test-Path $path)) { throw 'HomeWatch.ps1 was not found.' };" ^
  "$text = [IO.File]::ReadAllText($path);" ^
  "if ($text -match '\$EventDiskCachePath') { Write-Host 'Persistent event cache is already installed.'; exit 0 };" ^
  "$backup = $path + '.before-persistent-cache-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak';" ^
  "Copy-Item $path $backup -Force;" ^
  "$nl = [Environment]::NewLine;" ^
  "$old1 = '$EventsPath = Join-Path $DataDir ''events.jsonl''';" ^
  "$new1 = $old1 + $nl + '$EventDiskCachePath = Join-Path $DataDir ''event-view-cache.clixml''';" ^
  "if (-not $text.Contains($old1)) { throw 'Could not find the EventsPath definition.' };" ^
  "$text = $text.Replace($old1,$new1);" ^
  "$old2 = '    if($script:EventViewCache.ContainsKey($cacheKey)){return @($script:EventViewCache[$cacheKey])}';" ^
  "$new2 = @( '    if($script:EventViewCache.ContainsKey($cacheKey)){return @($script:EventViewCache[$cacheKey])}', '    if (Test-Path $EventDiskCachePath) {', '        try {', '            $diskCache = Import-Clixml -Path $EventDiskCachePath', '            if ([int]$diskCache.hours -eq $Hours -and [int64]$diskCache.eventStamp -eq [int64]$eventStamp -and [int64]$diskCache.configStamp -eq [int64]$configStamp) {', '                $diskResult = @($diskCache.events)', '                $script:EventViewCache = @{$cacheKey=$diskResult}', '                Write-Host (''Event cache: loaded {0} events from disk'' -f $diskResult.Count)', '                return $diskResult', '            }', '        } catch {', '            Write-Warning (''Could not load persistent event cache: '' + $_.Exception.Message)', '        }', '    }' ) -join $nl;" ^
  "if (-not $text.Contains($old2)) { throw 'Could not find the in-memory cache check.' };" ^
  "$text = $text.Replace($old2,$new2);" ^
  "$old3 = '    $result=@($items | Sort-Object time -Descending);$script:EventViewCache=@{$cacheKey=$result};return $result';" ^
  "$new3 = @( '    $result=@($items | Sort-Object time -Descending)', '    $script:EventViewCache=@{$cacheKey=$result}', '    try {', '        $cacheTemp = $EventDiskCachePath + ''.tmp''', '        [pscustomobject]@{', '            hours = $Hours', '            eventStamp = [int64]$eventStamp', '            configStamp = [int64]$configStamp', '            createdAt = [DateTimeOffset]::Now.ToString(''o'')', '            events = $result', '        } | Export-Clixml -Path $cacheTemp -Depth 8', '        Move-Item $cacheTemp $EventDiskCachePath -Force', '        Write-Host (''Event cache: saved {0} events to disk'' -f $result.Count)', '    } catch {', '        Write-Warning (''Could not save persistent event cache: '' + $_.Exception.Message)', '    }', '    return $result' ) -join $nl;" ^
  "if (-not $text.Contains($old3)) { throw 'Could not find the Get-Events return statement.' };" ^
  "$text = $text.Replace($old3,$new3);" ^
  "[IO.File]::WriteAllText($path,$text,(New-Object Text.UTF8Encoding($false)));" ^
  "Write-Host ('Persistent event cache installed. Backup: ' + $backup) -ForegroundColor Green;"

if errorlevel 1 (
  echo.
  echo The cache update failed. HomeWatch.ps1 was not intentionally changed.
  pause
  exit /b 1
)

echo.
echo Done. Restart HomeWatch, load the dashboard once, then restart it again to test the disk cache.
pause
