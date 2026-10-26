@echo off
rem ===================================================================
rem  Qoder-Status-TypeBar-AutoInstaller -- build
rem
rem  用 .NET Framework 自带的 csc.exe 编译，不需要 Visual Studio。
rem  输出：Install-Qoder-Status-TypeBar.exe
rem ===================================================================
setlocal
cd /d "%~dp0"

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo [x] 找不到 csc.exe: "%CSC%"
    echo     .NET Framework 4 应该自带它，检查一下 Windows 安装是否完整。
    exit /b 1
)

echo   编译 Install-Qoder-Status-TypeBar.exe ...
"%CSC%" -nologo -target:exe -optimize+ -codepage:65001 ^
    -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll ^
    -out:"Install-Qoder-Status-TypeBar.exe" "src\Program.cs"

if errorlevel 1 (
    echo.
    echo [x] 编译失败。
    exit /b 1
)

echo.
echo   完成: %~dp0Install-Qoder-Status-TypeBar.exe
echo.
exit /b 0
