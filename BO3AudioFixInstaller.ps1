# ============================================================================
#  Black Ops III Audio Fix - Installer
#  Pick which audio devices Black Ops III is allowed to see, then install.
#  Windows and every other app keep seeing ALL your devices; only BO3's view
#  is filtered, by a proxy winmm.dll placed in the game folder.
# ============================================================================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# ---------------------------------------------------------------- native API
$csharp = @'
using System;
using System.Runtime.InteropServices;
public static class MM {
  [StructLayout(LayoutKind.Sequential, Pack=1, CharSet=CharSet.Unicode)]
  public struct WAVEOUTCAPSW { public ushort wMid; public ushort wPid; public uint vDriverVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szPname;
    public uint dwFormats; public ushort wChannels; public ushort wReserved1; public uint dwSupport; }
  [StructLayout(LayoutKind.Sequential, Pack=1, CharSet=CharSet.Unicode)]
  public struct WAVEINCAPSW { public ushort wMid; public ushort wPid; public uint vDriverVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szPname;
    public uint dwFormats; public ushort wChannels; public ushort wReserved1; }
  [StructLayout(LayoutKind.Sequential, Pack=1, CharSet=CharSet.Unicode)]
  public struct MIXERCAPSW { public ushort wMid; public ushort wPid; public uint vDriverVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szPname;
    public uint fdwSupport; public uint cDestinations; }
  [DllImport("winmm.dll")] public static extern uint waveOutGetNumDevs();
  [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
  [DllImport("winmm.dll")] public static extern uint mixerGetNumDevs();
  [DllImport("winmm.dll", CharSet=CharSet.Unicode)] public static extern int waveOutGetDevCapsW(IntPtr id, ref WAVEOUTCAPSW c, uint cb);
  [DllImport("winmm.dll", CharSet=CharSet.Unicode)] public static extern int waveInGetDevCapsW(IntPtr id, ref WAVEINCAPSW c, uint cb);
  [DllImport("winmm.dll", CharSet=CharSet.Unicode)] public static extern int mixerGetDevCapsW(IntPtr id, ref MIXERCAPSW c, uint cb);
}
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] public class MMDevEnumCo {}
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator {
  [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr col);
  [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice {
  [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
  [PreserveSig] int OpenPropertyStore(int acc, out IntPtr props);
  [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}
public static class AudioDefault {
  public static string Id(int flow) {
    try {
      var e = (IMMDeviceEnumerator)(new MMDevEnumCo());
      IMMDevice d; if (e.GetDefaultAudioEndpoint(flow, 0, out d) != 0 || d == null) return null;
      string id; d.GetId(out id); return id;
    } catch { return null; }
  }
}
'@
if (-not ('MM' -as [type])) { Add-Type -TypeDefinition $csharp }

# ------------------------------------------------------- default device names
function Get-DefaultEndpointName([int]$flow) {
    try {
        $id = [AudioDefault]::Id($flow)
        if (-not $id) { return $null }
        $g = '{' + (($id -split '\}\.\{')[-1])
        $flowName = if ($flow -eq 0) { 'Render' } else { 'Capture' }
        $pr = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\$flowName\$g\Properties" -ErrorAction SilentlyContinue
        return $pr.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'
    } catch { return $null }
}
$DefaultOut = Get-DefaultEndpointName 0
$DefaultIn  = Get-DefaultEndpointName 1

# ------------------------------------------------------------ device scanning
function Get-AudioDevices {
    $map = [ordered]@{}
    function Add-Dev($name, $kind) {
        if ([string]::IsNullOrWhiteSpace($name)) { return }
        if (-not $map.Contains($name)) {
            $map[$name] = [pscustomobject]@{ Name = $name; Playback = $false; Recording = $false; Mixer = $false }
        }
        $map[$name].$kind = $true
    }
    $c1 = New-Object MM+WAVEOUTCAPSW
    for ($i = 0; $i -lt [MM]::waveOutGetNumDevs(); $i++) {
        if ([MM]::waveOutGetDevCapsW([IntPtr]$i, [ref]$c1, [uint32][Runtime.InteropServices.Marshal]::SizeOf($c1)) -eq 0) { Add-Dev $c1.szPname 'Playback' }
    }
    $c2 = New-Object MM+WAVEINCAPSW
    for ($i = 0; $i -lt [MM]::waveInGetNumDevs(); $i++) {
        if ([MM]::waveInGetDevCapsW([IntPtr]$i, [ref]$c2, [uint32][Runtime.InteropServices.Marshal]::SizeOf($c2)) -eq 0) { Add-Dev $c2.szPname 'Recording' }
    }
    $c3 = New-Object MM+MIXERCAPSW
    for ($i = 0; $i -lt [MM]::mixerGetNumDevs(); $i++) {
        if ([MM]::mixerGetDevCapsW([IntPtr]$i, [ref]$c3, [uint32][Runtime.InteropServices.Marshal]::SizeOf($c3)) -eq 0) { Add-Dev $c3.szPname 'Mixer' }
    }
    return @($map.Values)
}

# names that are almost always virtual / not needed by the game
$VirtualPatterns = @('voicemeeter','vb-audio','cable','virtual','oculus','sonar','steelseries',
                     'krisp','voicemod','loopback','steam streaming','splitcam','ivcam','parsec',
                     'obsbot','nvidia high','meta ','vrchat','wave link','banana','potato')

function Test-IsVirtual($name) {
    $n = $name.ToLower()
    foreach ($p in $VirtualPatterns) { if ($n.Contains($p)) { return $true } }
    return $false
}
function Test-IsDefault($name) {
    foreach ($d in @($DefaultOut, $DefaultIn)) {
        if ($d -and $name.ToLower().StartsWith($d.ToLower())) { return $true }
    }
    return $false
}

# ------------------------------------------------------------ BO3 path finder
function Find-Bo3Path {
    $cands = New-Object System.Collections.Generic.List[string]
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
        if ($steam) {
            $steam = $steam -replace '/', '\'
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path $vdf) {
                foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s*"([^"]+)"')) {
                    $cands.Add(($m.Groups[1].Value -replace '\\\\', '\'))
                }
            }
            $cands.Add($steam)
        }
    } catch {}
    foreach ($d in @('C:','D:','E:','F:','G:')) {
        $cands.Add("$d\SteamLibrary"); $cands.Add("$d\Steam"); $cands.Add("$d\Program Files (x86)\Steam")
    }
    foreach ($c in $cands) {
        $p = Join-Path $c 'steamapps\common\Call of Duty Black Ops III'
        if (Test-Path (Join-Path $p 'BlackOps3.exe')) { return $p }
    }
    return ''
}

# ====================================================================== GUI ==
$form                 = New-Object System.Windows.Forms.Form
$form.Text            = 'Black Ops III Audio Fix - Installer'
$form.Size            = New-Object System.Drawing.Size(860, 700)
$form.StartPosition   = 'CenterScreen'
$form.MinimumSize     = New-Object System.Drawing.Size(700, 560)
$form.Font            = New-Object System.Drawing.Font('Segoe UI', 9)

$intro                = New-Object System.Windows.Forms.Label
$intro.Text           = "Black Ops III freezes on a black screen when too many audio devices are enabled. Tick only the devices the game needs to see. Everything stays enabled in Windows."
$intro.Location       = New-Object System.Drawing.Point(12, 10)
$intro.Size           = New-Object System.Drawing.Size(820, 34)
$intro.Anchor         = 'Top,Left,Right'
$form.Controls.Add($intro)

# --- game folder row
$lblPath              = New-Object System.Windows.Forms.Label
$lblPath.Text         = 'Black Ops III folder:'
$lblPath.Location     = New-Object System.Drawing.Point(12, 52)
$lblPath.Size         = New-Object System.Drawing.Size(120, 20)
$form.Controls.Add($lblPath)

$txtPath              = New-Object System.Windows.Forms.TextBox
$txtPath.Location     = New-Object System.Drawing.Point(135, 50)
$txtPath.Size         = New-Object System.Drawing.Size(600, 22)
$txtPath.Anchor       = 'Top,Left,Right'
$form.Controls.Add($txtPath)

$btnBrowse            = New-Object System.Windows.Forms.Button
$btnBrowse.Text       = 'Browse...'
$btnBrowse.Location   = New-Object System.Drawing.Point(742, 48)
$btnBrowse.Size       = New-Object System.Drawing.Size(90, 26)
$btnBrowse.Anchor     = 'Top,Right'
$form.Controls.Add($btnBrowse)

# --- device list
$lblList              = New-Object System.Windows.Forms.Label
$lblList.Text         = 'Audio devices - ticked = Black Ops III can see it:'
$lblList.Location     = New-Object System.Drawing.Point(12, 84)
$lblList.Size         = New-Object System.Drawing.Size(500, 20)
$form.Controls.Add($lblList)

$list                 = New-Object System.Windows.Forms.ListView
$list.Location        = New-Object System.Drawing.Point(12, 106)
$list.Size            = New-Object System.Drawing.Size(820, 380)
$list.Anchor          = 'Top,Bottom,Left,Right'
$list.View            = 'Details'
$list.CheckBoxes      = $true
$list.FullRowSelect   = $true
$list.GridLines       = $true
[void]$list.Columns.Add('Device (as the game sees it)', 400)
[void]$list.Columns.Add('Type', 150)
[void]$list.Columns.Add('Notes', 250)
$form.Controls.Add($list)

# --- selection buttons
$btnRec               = New-Object System.Windows.Forms.Button
$btnRec.Text          = 'Recommended'
$btnRec.Location      = New-Object System.Drawing.Point(12, 494)
$btnRec.Size          = New-Object System.Drawing.Size(110, 26)
$btnRec.Anchor        = 'Bottom,Left'
$form.Controls.Add($btnRec)

$btnNone              = New-Object System.Windows.Forms.Button
$btnNone.Text         = 'Untick all'
$btnNone.Location     = New-Object System.Drawing.Point(128, 494)
$btnNone.Size         = New-Object System.Drawing.Size(90, 26)
$btnNone.Anchor       = 'Bottom,Left'
$form.Controls.Add($btnNone)

$btnAll               = New-Object System.Windows.Forms.Button
$btnAll.Text          = 'Tick all'
$btnAll.Location      = New-Object System.Drawing.Point(224, 494)
$btnAll.Size          = New-Object System.Drawing.Size(90, 26)
$btnAll.Anchor        = 'Bottom,Left'
$form.Controls.Add($btnAll)

$btnRefresh           = New-Object System.Windows.Forms.Button
$btnRefresh.Text      = 'Rescan devices'
$btnRefresh.Location  = New-Object System.Drawing.Point(320, 494)
$btnRefresh.Size      = New-Object System.Drawing.Size(110, 26)
$btnRefresh.Anchor    = 'Bottom,Left'
$form.Controls.Add($btnRefresh)

$lblCount             = New-Object System.Windows.Forms.Label
$lblCount.Location    = New-Object System.Drawing.Point(440, 498)
$lblCount.Size        = New-Object System.Drawing.Size(392, 20)
$lblCount.Anchor      = 'Bottom,Left,Right'
$lblCount.Font        = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($lblCount)

# --- status box
$txtLog               = New-Object System.Windows.Forms.TextBox
$txtLog.Location      = New-Object System.Drawing.Point(12, 528)
$txtLog.Size          = New-Object System.Drawing.Size(820, 84)
$txtLog.Anchor        = 'Bottom,Left,Right'
$txtLog.Multiline     = $true
$txtLog.ReadOnly      = $true
$txtLog.ScrollBars    = 'Vertical'
$txtLog.BackColor     = [System.Drawing.Color]::WhiteSmoke
$form.Controls.Add($txtLog)

# --- action buttons
$btnInstall           = New-Object System.Windows.Forms.Button
$btnInstall.Text      = 'Install'
$btnInstall.Location  = New-Object System.Drawing.Point(566, 620)
$btnInstall.Size      = New-Object System.Drawing.Size(120, 32)
$btnInstall.Anchor    = 'Bottom,Right'
$form.Controls.Add($btnInstall)

$btnUninstall         = New-Object System.Windows.Forms.Button
$btnUninstall.Text    = 'Uninstall'
$btnUninstall.Location= New-Object System.Drawing.Point(438, 620)
$btnUninstall.Size    = New-Object System.Drawing.Size(120, 32)
$btnUninstall.Anchor  = 'Bottom,Right'
$form.Controls.Add($btnUninstall)

$btnClose             = New-Object System.Windows.Forms.Button
$btnClose.Text        = 'Close'
$btnClose.Location    = New-Object System.Drawing.Point(712, 620)
$btnClose.Size        = New-Object System.Drawing.Size(120, 32)
$btnClose.Anchor      = 'Bottom,Right'
$form.Controls.Add($btnClose)

# ================================================================= behaviour =
function Write-Log($msg) {
    $txtLog.AppendText("$msg`r`n")
    $txtLog.SelectionStart = $txtLog.TextLength
    $txtLog.ScrollToCaret()
}

function Update-Count {
    $n = @($list.Items | Where-Object { $_.Checked }).Count
    $lblCount.Text = "Black Ops III will see $n device(s)"
    if ($n -eq 0)      { $lblCount.ForeColor = [System.Drawing.Color]::Firebrick;  $lblCount.Text += '  - the game needs at least one!' }
    elseif ($n -le 8)  { $lblCount.ForeColor = [System.Drawing.Color]::ForestGreen; $lblCount.Text += '  - good' }
    elseif ($n -le 14) { $lblCount.ForeColor = [System.Drawing.Color]::DarkOrange;  $lblCount.Text += '  - should be OK, fewer is safer' }
    else               { $lblCount.ForeColor = [System.Drawing.Color]::Firebrick;   $lblCount.Text += '  - too many, the game may still hang' }
}

function Get-InstalledKeeps($gameDir) {
    $cfg = Join-Path $gameDir 'bo3_audio.cfg'
    $keeps = @()
    if (Test-Path $cfg) {
        foreach ($line in (Get-Content $cfg)) {
            $t = $line.Trim()
            if ($t -match '^(?i)keep\s*=\s*(.+)$') { $keeps += $Matches[1].Trim() }
        }
    }
    return $keeps
}

$script:Devices = @()

function Refresh-Devices {
    $list.BeginUpdate()
    $list.Items.Clear()
    try {
        $script:Devices = Get-AudioDevices
    } catch {
        $script:Devices = @()
        Write-Log "ERROR reading audio devices: $($_.Exception.Message)"
    }
    $existing = @()
    if ($txtPath.Text -and (Test-Path $txtPath.Text)) { $existing = Get-InstalledKeeps $txtPath.Text }
    foreach ($d in $script:Devices) {
        $kinds = @()
        if ($d.Playback)  { $kinds += 'Playback' }
        if ($d.Recording) { $kinds += 'Recording' }
        if ($d.Mixer)     { $kinds += 'Mixer' }
        $notes = @()
        if (Test-IsDefault  $d.Name) { $notes += 'Windows default' }
        if (Test-IsVirtual  $d.Name) { $notes += 'virtual' }
        $item = New-Object System.Windows.Forms.ListViewItem($d.Name)
        [void]$item.SubItems.Add(($kinds -join ', '))
        [void]$item.SubItems.Add(($notes -join ', '))
        if ($existing.Count -gt 0) {
            foreach ($k in $existing) { if ($k -and $d.Name.ToLower().Contains($k.ToLower())) { $item.Checked = $true } }
        } else {
            $item.Checked = ((-not (Test-IsVirtual $d.Name)) -or (Test-IsDefault $d.Name))
        }
        if (Test-IsDefault $d.Name) { $item.ForeColor = [System.Drawing.Color]::DarkGreen }
        [void]$list.Items.Add($item)
    }
    $list.EndUpdate()
    Update-Count
    $srcMsg = if ($existing.Count -gt 0) { 'ticks loaded from the installed bo3_audio.cfg' } else { 'ticks set to the recommended selection' }
    Write-Log ("Found {0} audio device name(s) - {1}." -f $script:Devices.Count, $srcMsg)
}

$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Select the Call of Duty Black Ops III folder (the one containing BlackOps3.exe)'
    if ($txtPath.Text -and (Test-Path $txtPath.Text)) { $dlg.SelectedPath = $txtPath.Text }
    if ($dlg.ShowDialog() -eq 'OK') {
        $txtPath.Text = $dlg.SelectedPath
        if (-not (Test-Path (Join-Path $dlg.SelectedPath 'BlackOps3.exe'))) {
            Write-Log 'WARNING: BlackOps3.exe was not found in that folder.'
        } else { Refresh-Devices }
    }
})

$btnRec.Add_Click({
    foreach ($i in $list.Items) {
        $i.Checked = ((-not (Test-IsVirtual $i.Text)) -or (Test-IsDefault $i.Text))
    }
    Update-Count
})
$btnNone.Add_Click({ foreach ($i in $list.Items) { $i.Checked = $false }; Update-Count })
$btnAll.Add_Click({  foreach ($i in $list.Items) { $i.Checked = $true  }; Update-Count })
$btnRefresh.Add_Click({ Refresh-Devices })
$list.Add_ItemChecked({ Update-Count })
$btnClose.Add_Click({ $form.Close() })

$btnInstall.Add_Click({
    $game = $txtPath.Text.Trim()
    if (-not $game -or -not (Test-Path (Join-Path $game 'BlackOps3.exe'))) {
        [System.Windows.Forms.MessageBox]::Show('Pick the Black Ops III folder first (it must contain BlackOps3.exe).','Installer',0,48) | Out-Null
        return
    }
    if (Get-Process -Name 'BlackOps3' -ErrorAction SilentlyContinue) {
        [System.Windows.Forms.MessageBox]::Show('Black Ops III is running. Close the game first.','Installer',0,48) | Out-Null
        return
    }
    $keeps = @($list.Items | Where-Object { $_.Checked } | ForEach-Object { $_.Text })
    if ($keeps.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show('Tick at least one device, or the game will have no audio devices at all.','Installer',0,48) | Out-Null
        return
    }
    if ($DefaultOut -and -not ($keeps | Where-Object { $_.ToLower().StartsWith($DefaultOut.ToLower()) })) {
        $r = [System.Windows.Forms.MessageBox]::Show(
            "Your Windows default playback device ($DefaultOut) is not ticked.`r`n`r`nThe game will normally still play through it, but ticking it is safer. Install anyway?",
            'Installer', 4, 48)
        if ($r -ne 'Yes') { return }
    }
    try {
        $payload = Join-Path $ScriptDir 'payload\winmm.dll'
        if (-not (Test-Path $payload)) { throw "payload\winmm.dll is missing from $ScriptDir" }

        $cfgPath = Join-Path $game 'bo3_audio.cfg'
        if (Test-Path $cfgPath) {
            $bak = "$cfgPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
            Copy-Item $cfgPath $bak -Force
            Write-Log "Backed up existing config -> $(Split-Path -Leaf $bak)"
        }

        Copy-Item $payload (Join-Path $game 'winmm.dll') -Force
        Write-Log 'Installed winmm.dll (the filter).'

        Copy-Item "$env:WINDIR\System32\winmm.dll" (Join-Path $game 'winmm_real.dll') -Force
        Write-Log 'Installed winmm_real.dll (fresh copy of the real Windows winmm).'

        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add('# Black Ops III audio-device filter - generated by the installer')
        $lines.Add('# ' + (Get-Date -Format 'yyyy-MM-dd HH:mm'))
        $lines.Add('# Only devices matching a keep= line are visible to the game.')
        $lines.Add('# Re-run the installer to change this, or edit by hand.')
        $lines.Add('')
        foreach ($k in $keeps) { $lines.Add("keep=$k") }
        $lines.Add('')
        $lines.Add('# set log=1 to have the game write bo3_audio.log listing what it saw')
        $lines.Add('log=0')
        [System.IO.File]::WriteAllLines($cfgPath, $lines, [System.Text.Encoding]::Default)
        Write-Log "Wrote bo3_audio.cfg with $($keeps.Count) allowed device(s)."
        Write-Log 'DONE - launch Black Ops III normally.'
        [System.Windows.Forms.MessageBox]::Show("Installed.`r`n`r`nBlack Ops III will now see $($keeps.Count) audio device(s). Everything stays enabled in Windows.",'Installer',0,64) | Out-Null
    } catch {
        Write-Log "ERROR: $($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show("Install failed:`r`n$($_.Exception.Message)",'Installer',0,16) | Out-Null
    }
})

$btnUninstall.Add_Click({
    $game = $txtPath.Text.Trim()
    if (-not $game -or -not (Test-Path $game)) {
        [System.Windows.Forms.MessageBox]::Show('Pick the Black Ops III folder first.','Installer',0,48) | Out-Null
        return
    }
    if (Get-Process -Name 'BlackOps3' -ErrorAction SilentlyContinue) {
        [System.Windows.Forms.MessageBox]::Show('Black Ops III is running. Close the game first.','Installer',0,48) | Out-Null
        return
    }
    $r = [System.Windows.Forms.MessageBox]::Show(
        "Remove winmm.dll, winmm_real.dll, bo3_audio.cfg and bo3_audio.log from:`r`n$game`r`n`r`nThe game goes back to stock behaviour (and may black-screen again).",
        'Uninstall', 4, 48)
    if ($r -ne 'Yes') { return }
    foreach ($f in 'winmm.dll','winmm_real.dll','bo3_audio.cfg','bo3_audio.log') {
        $p = Join-Path $game $f
        if (Test-Path $p) {
            try { Remove-Item -LiteralPath $p -Force; Write-Log "Removed $f" }
            catch { Write-Log "Could not remove $f : $($_.Exception.Message)" }
        }
    }
    Write-Log 'Uninstall finished.'
})

# ------------------------------------------------------------------- startup
$found = Find-Bo3Path
if ($found) { $txtPath.Text = $found; Write-Log "Found Black Ops III at: $found" }
else        { Write-Log 'Could not find Black Ops III automatically - use Browse...' }
Refresh-Devices

[void]$form.ShowDialog()
