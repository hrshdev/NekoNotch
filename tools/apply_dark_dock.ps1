# One UI 8.5 Dark Dock Configuration for Windhawk (windows-11-taskbar-styler)
# Run as Administrator or allow UAC elevation.

$regPath = "HKLM:\SOFTWARE\Windhawk\Engine\Mods\windows-11-taskbar-styler\Settings"

if (!(Test-Path $regPath)) {
    New-Item -Path $regPath -Force | Out-Null
}

Write-Host "Configuring Windhawk One UI 8.5 Dark Dock..." -ForegroundColor Cyan

# 1. Theme: One UI 8.5 (Dock)
Set-ItemProperty -Path $regPath -Name "theme" -Value "One_UI_8_5_variant_Dock"

# 2. Dark theme squircle icon background (darkbg.png from One UI 8.5 assets)
Set-ItemProperty -Path $regPath -Name "styleConstants[0]" -Value 'IconBackground=<ImageBrush Stretch="Uniform"> <ImageBrush.ImageSource> <BitmapImage UriSource="https://raw.githubusercontent.com/ramensoftware/windows-11-taskbar-styling-guide/refs/heads/main/Themes/One%20UI%208.5/Assets/darkbg.png" DecodePixelType="Logical" DecodePixelWidth="64" DecodePixelHeight="64" /> </ImageBrush.ImageSource> </ImageBrush>'

# 3. Collapse system tray completely
Set-ItemProperty -Path $regPath -Name "controlStyles[0].target" -Value "Taskbar.TaskbarExtensionElement"
Set-ItemProperty -Path $regPath -Name "controlStyles[0].styles[0]" -Value "Visibility=Collapsed"

# 4. Dock background: symmetric rounded capsule pill
Set-ItemProperty -Path $regPath -Name "controlStyles[1].target" -Value "Grid#RootGrid > Taskbar.TaskbarBackground > Grid"
Set-ItemProperty -Path $regPath -Name "controlStyles[1].styles[0]" -Value "CornerRadius=25"
Set-ItemProperty -Path $regPath -Name "controlStyles[1].styles[1]" -Value "BorderThickness=1"

Write-Host "Restarting Windhawk service to apply changes..." -ForegroundColor Yellow
Restart-Service Windhawk -ErrorAction SilentlyContinue

Write-Host "Done! The macOS / One UI 8.5 Dark Dock with hidden tray is now active." -ForegroundColor Green
