# Measures the tray app's idle footprint and its hotkey-to-inbox latency, as reported in the README.
#   powershell -NoProfile -STA -ExecutionPolicy Bypass -File tools\measure.ps1 -Exe <ContextCompanion.exe> -Dir <empty folder>
# <Dir> needs settings.json ({"schema_version": 1, "app": {"inbox": "<Dir>\inbox", "preview": false}}) and
# settings-gesture.json (the same with "wheel_gesture": true). The script starts and stops the app, shows a test window
# of its own and presses the window and text hotkeys on it; leave the desktop alone while it runs (about 4 minutes).
param([string]$Exe, [string]$Dir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class In3 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static void Chord(byte key) {
    keybd_event(0x11,0,0,UIntPtr.Zero); keybd_event(0x12,0,0,UIntPtr.Zero); keybd_event(key,0,0,UIntPtr.Zero);
    keybd_event(key,0,2,UIntPtr.Zero); keybd_event(0x12,0,2,UIntPtr.Zero); keybd_event(0x11,0,2,UIntPtr.Zero);
  }
}
"@
[In3]::SetProcessDPIAware() | Out-Null
function Pump([int]$ms) { $end = [DateTime]::Now.AddMilliseconds($ms); while ([DateTime]::Now -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 5 } }

function Idle([string]$settings, [string]$label) {
  $p = Start-Process $Exe -ArgumentList @('--settings', "`"$settings`"") -PassThru
  Start-Sleep -Seconds 15
  $p.Refresh(); $cpu0 = $p.TotalProcessorTime; $t0 = [DateTime]::UtcNow
  $ws = @(); $priv = @()
  for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Seconds 1; $p.Refresh(); $ws += $p.WorkingSet64; $priv += $p.PrivateMemorySize64 }
  $p.Refresh(); $cpu = ($p.TotalProcessorTime - $cpu0).TotalMilliseconds; $wall = ([DateTime]::UtcNow - $t0).TotalMilliseconds
  $cores = [Environment]::ProcessorCount
  '{0}: CPU {1:N1} ms over {2:N0} s ({3:N3} % of one core), working set median {4:N1} MB, private bytes median {5:N1} MB, threads {6}' -f $label, $cpu, ($wall/1000), (100*$cpu/$wall), (($ws | Sort-Object)[30]/1MB), (($priv | Sort-Object)[30]/1MB), $p.Threads.Count
  Stop-Process -Id $p.Id; Start-Sleep -Seconds 2
}

Idle "$Dir\settings.json" 'idle, hotkeys only'
Idle "$Dir\settings-gesture.json" 'idle, hotkeys and wheel-gesture hook'

# Latency: hotkey pressed -> packet folder with manifest.json in the inbox (preview off)
$inbox = "$Dir\inbox"
$p = Start-Process $Exe -ArgumentList @('--settings', "`"$Dir\settings.json`"") -PassThru
Start-Sleep -Seconds 5
$form = New-Object System.Windows.Forms.Form
$form.Text = 'cc-latency'; $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(200, 200)
$form.Size = New-Object System.Drawing.Size(1280, 720); $form.TopMost = $true
$box = New-Object System.Windows.Forms.TextBox; $box.Multiline = $true; $box.Size = New-Object System.Drawing.Size(600, 300); $form.Controls.Add($box)
$form.Show(); Pump 500
foreach ($kind in @('window', 'text')) {
  $times = @()
  for ($i = 0; $i -lt 10; $i++) {
    $form.BackColor = [System.Drawing.Color]::FromArgb(($i * 23) % 256, 90, 160)
    $box.Text = "latency sample $kind $i " + [guid]::NewGuid(); $form.Activate(); [In3]::SetForegroundWindow($form.Handle) | Out-Null; Pump 400
    if ($kind -eq 'text') { $box.Focus() | Out-Null; $box.SelectAll(); Pump 100 }
    $before = @(Get-ChildItem $inbox -Directory -ErrorAction SilentlyContinue | Where-Object Name -notlike '.*').Count
    $sw = [Diagnostics.Stopwatch]::StartNew()
    [In3]::Chord($(if ($kind -eq 'window') { 0x57 } else { 0x54 }))
    while ($sw.ElapsedMilliseconds -lt 10000) {
      $now = @(Get-ChildItem $inbox -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '.*' -and (Test-Path "$($_.FullName)\manifest.json") }).Count
      if ($now -gt $before) { break }
      Pump 5
    }
    $times += $sw.ElapsedMilliseconds
    Pump 800
  }
  $sorted = $times | Sort-Object
  '{0} hotkey -> inbox: median {1} ms, min {2} ms, max {3} ms over {4} runs ({5})' -f $kind, $sorted[5], $sorted[0], $sorted[-1], $times.Count, ($times -join ', ')
}
$form.Close(); Stop-Process -Id $p.Id
