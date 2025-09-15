# speed_publisher.ps1
#add this to terminal to start:  Unblock-File .\speed_publisher.ps1     
#powershell -NoProfile -ExecutionPolicy Bypass -File .\cars_publisher.ps1


param(
  [string] $Broker      = "127.0.0.1",
  [int]    $Port        = 1883,
  [string] $Topic       = "factory/belt/speed",
  [int]    $IntervalSec = 2,     # 0 dışındaki hızlar için normal aralık
  [int]    $Count       = 0,     # 0 => sonsuz
  [string] $MosqPubPath = "C:\Program Files\mosquitto\mosquitto_pub.exe",
  [switch] $AsJson,              # -AsJson dersen {"speed":N} gönderir; yoksa sadece N
  [switch] $Retain,              # -Retain dersen retained gönderir
  [switch] $WaitBeforeFirstSend, # İlk mesajdan önce bekle
  [switch] $ClearRetained        # Retained temizle ve çık
)

# --- Kontrol ---
if (-not (Test-Path $MosqPubPath)) {
  Write-Error "mosquitto_pub not found: $MosqPubPath"
  exit 2
}

# --- Retained temizleme (isteğe bağlı) ---
if ($ClearRetained) {
  & "$MosqPubPath" -h $Broker -p $Port -t $Topic -r -n
  Write-Host "Cleared retained on '$Topic'."
  exit 0
}

Write-Host ("Starting speed publisher -> {0}:{1} topic '{2}' (IntervalSec={3})" -f $Broker,$Port,$Topic,$IntervalSec)

if ($WaitBeforeFirstSend) { Start-Sleep -Seconds $IntervalSec }

[int]$sent = 0
try {
  while ($true) {
    # 0..20 (Get-Random -Maximum 21 => 0-20 dahil)
    $speed = Get-Random -Minimum 0 -Maximum 21

    $payload = if ($AsJson) { "{""speed"":$speed}" } else { "$speed" }

    $args = @("-h",$Broker,"-p",$Port,"-t",$Topic,"-m",$payload)
    if ($Retain) { $args += "-r" }

    & "$MosqPubPath" @args
    Write-Host ("[SENT {0}] speed={1}" -f (Get-Date -Format "HH:mm:ss"), $speed)

    $sent++
    if ($Count -gt 0 -and $sent -ge $Count) { break }

    if ($speed -eq 0) {
      # 0 gelirse 5 sn dur
      Start-Sleep -Seconds 5
    } else {
      Start-Sleep -Seconds $IntervalSec
    }
  }
}
catch { Write-Error $_ }
finally { Write-Host "Done. Sent: $sent" }