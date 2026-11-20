[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [ValidateSet("system", "light", "dark")][string]$Theme = "dark",
  [ValidateSet("light", "dark", "")][string]$ToggleTo = "",
  [string]$Shot = "",
  [string]$ShotAfterToggle = "",
  [int]$TimeoutSeconds = 45,
  [int]$MinBrightness = -1,
  [int]$MaxBrightness = -1
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class LumenWindowProbe
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lparam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    public static List<IntPtr> WindowsOf(uint pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner == pid) { result.Add(hwnd); }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static string TitleOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
"@

$dataDir = Join-Path $env:LOCALAPPDATA "Lumen"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Set-Content -Path (Join-Path $dataDir "settings.json") -Encoding utf8 `
  -Value ("{""themeMode"":""$Theme""}")

if ($ToggleTo) {
  $env:LUMEN_SMOKE_TOGGLE_THEME = $ToggleTo
}
else {
  Remove-Item Env:\LUMEN_SMOKE_TOGGLE_THEME -ErrorAction SilentlyContinue
}

$process = Start-Process -FilePath $Exe -PassThru
try {
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  $main = [IntPtr]::Zero
  while ((Get-Date) -lt $deadline -and $main -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 400
    if ($process.HasExited) {
      throw "Lumen exited during startup with code $($process.ExitCode)."
    }

    foreach ($hwnd in [LumenWindowProbe]::WindowsOf([uint32]$process.Id)) {
      $title = [LumenWindowProbe]::TitleOf($hwnd)
      if ($title -eq "Lumen 遇到错误") {
        throw "Lumen raised a startup error dialog."
      }

      if ($title.StartsWith("Lumen") -and $title.Contains("流明")) {
        $main = $hwnd
      }
    }
  }

  if ($main -eq [IntPtr]::Zero) {
    throw "Main window did not appear within $TimeoutSeconds seconds."
  }

  Start-Sleep -Seconds 3
  foreach ($hwnd in [LumenWindowProbe]::WindowsOf([uint32]$process.Id)) {
    if ([LumenWindowProbe]::TitleOf($hwnd) -eq "Lumen 遇到错误") {
      throw "Lumen raised an error dialog after the window appeared."
    }
  }

  function Measure-Window([IntPtr]$handle, [string]$path) {
    $rect = New-Object LumenWindowProbe+RECT
    [void][LumenWindowProbe]::GetWindowRect($handle, [ref]$rect)
    $width = [Math]::Max($rect.Right - $rect.Left, 1)
    $height = [Math]::Max($rect.Bottom - $rect.Top, 1)

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
      [void][LumenWindowProbe]::PrintWindow($handle, $hdc, 2)
    }
    finally {
      $graphics.ReleaseHdc($hdc)
      $graphics.Dispose()
    }

    if ($path) {
      $parent = Split-Path -Parent $path
      if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
      $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }

    $total = 0.0
    $samples = 0
    for ($y = 0; $y -lt $height; $y += 8) {
      for ($x = 0; $x -lt $width; $x += 8) {
        $pixel = $bitmap.GetPixel($x, $y)
        $total += (0.299 * $pixel.R) + (0.587 * $pixel.G) + (0.114 * $pixel.B)
        $samples++
      }
    }
    $bitmap.Dispose()

    if ($samples -eq 0) { throw "Could not sample the window bitmap." }
    return [int]([Math]::Round($total / $samples))
  }

  $before = Measure-Window $main $Shot
  Write-Host "theme=$Theme brightness=$before"

  $brightness = $before
  if ($ToggleTo) {
    Start-Sleep -Seconds 9
    foreach ($hwnd in [LumenWindowProbe]::WindowsOf([uint32]$process.Id)) {
      if ([LumenWindowProbe]::TitleOf($hwnd) -eq "Lumen 遇到错误") {
        throw "Lumen raised an error dialog after switching themes."
      }
    }

    $brightness = Measure-Window $main $ShotAfterToggle
    Write-Host "toggled=$ToggleTo brightness=$brightness previous=$before"
    if ([Math]::Abs($brightness - $before) -lt 60) {
      throw "Switching to the $ToggleTo theme did not repaint the window (brightness $before -> $brightness)."
    }
  }

  if ($MinBrightness -ge 0 -and $brightness -lt $MinBrightness) {
    throw "Brightness $brightness is below the expected minimum $MinBrightness for the $Theme theme."
  }

  if ($MaxBrightness -ge 0 -and $brightness -gt $MaxBrightness) {
    throw "Brightness $brightness is above the expected maximum $MaxBrightness for the $Theme theme."
  }
}
finally {
  Remove-Item Env:\LUMEN_SMOKE_TOGGLE_THEME -ErrorAction SilentlyContinue
  if (-not $process.HasExited) {
    [void]$process.CloseMainWindow()
    Start-Sleep -Seconds 2
    if (-not $process.HasExited) { $process.Kill() }
  }
}
