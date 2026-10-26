<#
  安装器快捷方式还原回归测试（沙箱，不碰真实桌面）。

  用 --install-dir / --shortcut-dir 把安装目录和快捷方式目录都指到临时目录，
  跑四个场景：安装转换、卸载从备份恢复、备份丢失时重建为直接启动、
  用户自己改回去的快捷方式不被覆盖。

  用法（PowerShell）：
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-sandbox.ps1
  前置：先 build.bat 编译出 Install-Qoder-Status-TypeBar.exe，
        并准备好与 Program.cs 里 ZipSha256 一致的插件包 zip。
#>
param(
  [string]$Exe   = "$PSScriptRoot\..\Install-Qoder-Status-TypeBar.exe",
  [string]$Zip   = "$PSScriptRoot\..\..\Qoder-Status-TypeBar\dist\Qoder-Status-TypeBar-v1.0.3.zip",
  [string]$Qoder = "C:\Users\kybll\AppData\Local\Programs\Qoder\Qoder.exe",
  [string]$Sb    = "$env:TEMP\qoder-statusbar-installer-sandbox",
  # 传一个正在跑的注入器 PID，测试跑完断言它没被顺手杀掉（安装/卸载只该动本安装目录的进程）
  [string]$GuardPid = ""
)
$ErrorActionPreference = "Stop"
$lnkDir  = "$Sb\shortcuts"
$instDir = "$Sb\install"
$fail = 0

function Check($name, $cond) {
  if ($cond) { "  OK   $name" } else { "  FAIL $name"; $script:fail++ }
}
function Info($p) {
  $s = (New-Object -ComObject WScript.Shell).CreateShortcut($p)
  [pscustomobject]@{ Path = $p; Target = $s.TargetPath; Args = $s.Arguments; Desc = $s.Description }
}
function IsOurs($i) { ($i.Target -match 'wscript\.exe$') -and ($i.Args -match 'launch-hidden\.vbs') }
function IsPlain($i) { ($i.Target -ieq $Qoder) -and (-not (IsOurs $i)) }
function MakeLnk($p, $target, $desc) {
  $s = (New-Object -ComObject WScript.Shell).CreateShortcut($p)
  $s.TargetPath = $target; $s.Arguments = ""; $s.WorkingDirectory = Split-Path $target
  $s.IconLocation = "$target,0"; $s.Description = $desc; $s.Save()
}
function CleanSandbox {
  if (Test-Path $Sb) { Remove-Item $Sb -Recurse -Force }
  New-Item -ItemType Directory -Path "$lnkDir\StartMenu" -Force | Out-Null
  New-Item -ItemType Directory -Path $instDir -Force | Out-Null
  MakeLnk "$lnkDir\Qoder.lnk" $Qoder "orig-desktop"
  MakeLnk "$lnkDir\StartMenu\Qoder.lnk" $Qoder "orig-startmenu"
}
$common = @("--offline", $Zip, "--install-dir", $instDir, "--shortcut-dir", $lnkDir, "--port", "9555", "--no-wait")

if (-not (Test-Path $Exe)) { "[x] 先运行 build.bat 编译安装器: $Exe"; exit 2 }
if (-not (Test-Path $Zip)) { "[x] 找不到插件包 zip: $Zip"; exit 2 }
if (-not (Test-Path $Qoder)) { "[x] 找不到 Qoder.exe: $Qoder（用 -Qoder 指定）"; exit 2 }

"=== 场景 1：安装转换 + 备份 ==="
CleanSandbox
& $Exe @common --no-restart | Out-Null
$d = Info "$lnkDir\Qoder.lnk"; $m = Info "$lnkDir\StartMenu\Qoder.lnk"
Check "桌面快捷方式变成注入启动" (IsOurs $d)
Check "开始菜单快捷方式也变成注入启动" (IsOurs $m)
Check "桌面有原始备份" (Test-Path "$lnkDir\Qoder（原始启动）.lnk")
Check "开始菜单有原始备份" (Test-Path "$lnkDir\StartMenu\Qoder（原始启动）.lnk")
Check "port.txt = 9555" ((Get-Content "$instDir\port.txt" -Raw).Trim() -eq "9555")

"=== 场景 2：卸载从备份恢复（含子目录发现） ==="
& $Exe --uninstall --install-dir $instDir --shortcut-dir $lnkDir --no-wait | Out-Null
$d = Info "$lnkDir\Qoder.lnk"; $m = Info "$lnkDir\StartMenu\Qoder.lnk"
Check "桌面恢复为原始 Qoder.exe" (IsPlain $d)
Check "开始菜单也恢复为原始 Qoder.exe" (IsPlain $m)
Check "桌面备份已清理" (-not (Test-Path "$lnkDir\Qoder（原始启动）.lnk"))
Check "开始菜单备份已清理" (-not (Test-Path "$lnkDir\StartMenu\Qoder（原始启动）.lnk"))
Check "安装目录已删除" (-not (Test-Path $instDir))

"=== 场景 3：备份丢失时不删快捷方式，改回直接启动 ==="
CleanSandbox
& $Exe @common --no-restart | Out-Null
Remove-Item "$lnkDir\Qoder（原始启动）.lnk", "$lnkDir\StartMenu\Qoder（原始启动）.lnk" -Force
& $Exe --uninstall --install-dir $instDir --shortcut-dir $lnkDir --no-wait | Out-Null
$d = Info "$lnkDir\Qoder.lnk"; $m = Info "$lnkDir\StartMenu\Qoder.lnk"
Check "桌面快捷方式仍存在" (Test-Path "$lnkDir\Qoder.lnk")
Check "桌面快捷方式改回直接启动 Qoder" (IsPlain $d)
Check "开始菜单快捷方式改回直接启动 Qoder" (IsPlain $m)
Check "恢复后的快捷方式能真正指向 Qoder.exe" ((Get-Item $d.Target).FullName -ieq $Qoder)

"=== 场景 4：用户已自行改回的快捷方式不被覆盖 ==="
CleanSandbox
& $Exe @common --no-restart | Out-Null
MakeLnk "$lnkDir\StartMenu\Qoder.lnk" $Qoder "user-reverted"
& $Exe --uninstall --install-dir $instDir --shortcut-dir $lnkDir --no-wait | Out-Null
$d = Info "$lnkDir\Qoder.lnk"; $m = Info "$lnkDir\StartMenu\Qoder.lnk"
Check "桌面（我们的）从备份恢复" (($d.Desc -eq "orig-desktop") -and (IsPlain $d))
Check "开始菜单（用户自己改的）保持原样" (($m.Desc -eq "user-reverted") -and (IsPlain $m))
Check "开始菜单的原始备份没被顺手删掉" (Test-Path "$lnkDir\StartMenu\Qoder（原始启动）.lnk")

if ($GuardPid -ne "") {
  "=== 场景 5：目录外的注入器不被误杀 ==="
  $alive = $null
  try { $alive = Get-Process -Id ([int]$GuardPid) -ErrorAction Stop } catch {}
  Check "外部注入器 $GuardPid 还活着" ($null -ne $alive)
}

Remove-Item $Sb -Recurse -Force -ErrorAction SilentlyContinue
if ($fail -eq 0) { "`n全部通过" } else { "`n失败 $fail 项" }
exit $(if ($fail -eq 0) { 0 } else { 1 })
