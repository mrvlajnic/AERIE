param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Data
)

# A real start of the built browser: the window must appear, the chrome must be
# exposed to accessibility, and nothing may be written to errors.log. CI runs
# this so a crash at startup (or a build that only looks fine) fails the job.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

New-Item -ItemType Directory -Force $Data | Out-Null
# A local page: no network, no certificate, a title we can wait for.
$page = "data:text/html,<title>Smoke</title>"
$p = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList "--data=$Data", $page -PassThru

try
{
    $deadline = (Get-Date).AddSeconds(45)
    $title = ""
    while ((Get-Date) -lt $deadline)
    {
        Start-Sleep -Milliseconds 500
        $p.Refresh()
        if ($p.HasExited) { throw "browser exited early with code $($p.ExitCode)" }
        $title = $p.MainWindowTitle
        if ($title -like "Smoke*") { break }
    }
    if ($title -notlike "Smoke*") { throw "the test page did not load (title: '$title')" }

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $own = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $own)
    if ($null -eq $win) { throw "the window is not in the accessibility tree" }

    $button = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $button)
    if ($buttons.Count -lt 3) { throw "expected the chrome buttons, found $($buttons.Count)" }

    $log = Join-Path $Data "errors.log"
    if (Test-Path $log)
    {
        $text = Get-Content $log -Raw
        if ($text.Trim().Length -gt 0) { throw "errors.log is not empty:`n$text" }
    }

    "smoke OK: '$title', $($buttons.Count) accessible buttons"
}
finally
{
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
