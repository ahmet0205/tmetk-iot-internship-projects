param(
    [string] $Broker = "127.0.0.1",
    [int]    $Port = 1883,
    [string] $CarsTopic = "factory/cars",
    [string] $SpeedTopic = "factory/belt/speed",

    # SADECE SANİYE
    [int]    $IntervalSec = 2,          # örn: -IntervalSec 5
    [switch] $WaitBeforeFirstSend,         # ilk mesajdan önce bekle
    [int]    $Count = 0,           # 0 => sonsuz

    [string] $MosqPubPath = "C:\Program Files\mosquitto\mosquitto_pub.exe",
    [string] $ForceModel = "",          # "corolla" | "chr1" | "chr2" | "chr3"
    [string] $ForceColor = "",          # örn: "M35"
    [int]    $SpeedOnce = [int]::MinValue,  # örn: -SpeedOnce 12
    [switch] $ClearRetained                 # retained temizle ve çık
)

# --- Kontroller ---
if (-not (Test-Path $MosqPubPath)) {
    Write-Error "mosquitto_pub not found: $MosqPubPath"
    exit 2
}

# --- Veri setleri (Program.cs ile uyumlu) ---
$Models = @("corolla", "chr1", "chr2", "chr3")
$ColorCodesCommon = @("209", "040", "089", "1G3", "1K6", "1J6", "785", "3U5")
$ColorCodesCHR = @("2YB", "2NB", "2TB", "2MR", "2VU", "1L0", "M35")

function Get-RandomColorCode([string]$model) {
    if ($ForceColor) { return $ForceColor }
    if ($model -match '^chr') { return ($ColorCodesCHR + $ColorCodesCommon) | Get-Random }
    return $ColorCodesCommon | Get-Random
}

function New-VehId {
    $prefix = -join ((65..90) | Get-Random -Count 1 | ForEach-Object { [char]$_ })
    $num = Get-Random -Minimum 100 -Maximum 999
    $ts = (Get-Date -Format "HHmmssff")
    "$prefix$num$ts"
}

function Publish-Message([string]$topic, [string]$payload) {
    & "$MosqPubPath" -h $Broker -p $Port -t $topic -m $payload
}

# --- Retained temizleme ---
if ($ClearRetained) {
    & "$MosqPubPath" -h $Broker -p $Port -t $CarsTopic -r -n
    Write-Host "Cleared retained on '$CarsTopic'."
    exit 0
}

# --- Tek seferlik hız ---
if ($SpeedOnce -ne [int]::MinValue) {
    Publish-Message -topic $SpeedTopic -payload $SpeedOnce
    Write-Host ("[SENT speed] {0} -> {1}" -f $SpeedTopic, $SpeedOnce)
}

Write-Host ("Starting car publisher -> {0}:{1} topic '{2}' (IntervalSec={3}, WaitBeforeFirstSend={4})" `
        -f $Broker, $Port, $CarsTopic, $IntervalSec, [bool]$WaitBeforeFirstSend)

[int]$sent = 0

try {
    if ($WaitBeforeFirstSend) { Start-Sleep -Seconds $IntervalSec }

    while ($true) {
        $model = if ($ForceModel) { $ForceModel } else { $Models | Get-Random }
        $code = Get-RandomColorCode $model
        $vehId = New-VehId

        $payloadObj = [ordered]@{
            vehId        = $vehId
            modelId      = $model
            colorExtCode = $code
        }
        $json = $payloadObj | ConvertTo-Json -Compress

        Publish-Message -topic $CarsTopic -payload $json
        Write-Host ("[SENT {0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $json)

        $sent++
        if ($Count -gt 0 -and $sent -ge $Count) { break }

        Start-Sleep -Seconds $IntervalSec
    }
}
catch { Write-Error $_ }
finally { Write-Host "Done. Sent: $sent" }
```