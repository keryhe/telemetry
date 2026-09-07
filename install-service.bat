@echo off
setlocal enabledelayedexpansion

rem =====================================================================
rem Installs or updates the Keryhe.Telemetry.Server Windows Service.
rem
rem Usage:
rem   install-service.bat [source-publish-folder]
rem
rem   source-publish-folder  Folder containing the published output
rem                          (Keryhe.Telemetry.Server.exe and friends),
rem                          e.g. produced by:
rem                            dotnet publish src\Keryhe.Telemetry.Server ^
rem                              -c Release -r win-x64 --self-contained false -o publish
rem                          Defaults to a "publish" folder next to this
rem                          batch file if not given.
rem
rem Safe to re-run: if the service is already installed, this stops it,
rem syncs in the new files, and restarts it (update path). If it is not
rem installed yet, this creates it, configures it to start with Windows
rem and auto-restart on failure, syncs the files, and starts it.
rem =====================================================================

set "SERVICE_NAME=KeryheTelemetryServer"
set "DISPLAY_NAME=Keryhe Telemetry Server"
set "INSTALL_DIR=C:\Services\KeryheTelemetry"
set "EXE_PATH=%INSTALL_DIR%\Keryhe.Telemetry.Server.exe"

set "SOURCE_DIR=%~1"
if "%SOURCE_DIR%"=="" set "SOURCE_DIR=%~dp0publish"
rem Strip any trailing backslash so paths below don't end up with "\\"
if "%SOURCE_DIR:~-1%"=="\" set "SOURCE_DIR=%SOURCE_DIR:~0,-1%"

echo ============================================================
echo  Keryhe Telemetry Server - Windows Service install/update
echo ============================================================
echo  Service name : %SERVICE_NAME%
echo  Install dir  : %INSTALL_DIR%
echo  Source dir   : %SOURCE_DIR%
echo ============================================================
echo.

rem ---------------------------------------------------------------------
rem 1. Require Administrator privileges (service create/config/copy need it)
rem ---------------------------------------------------------------------
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo ERROR: This script must be run as Administrator.
    echo Right-click install-service.bat and choose "Run as administrator".
    exit /b 1
)

rem ---------------------------------------------------------------------
rem 2. Verify the source publish output exists
rem ---------------------------------------------------------------------
if not exist "%SOURCE_DIR%\Keryhe.Telemetry.Server.exe" (
    echo ERROR: "%SOURCE_DIR%\Keryhe.Telemetry.Server.exe" was not found.
    echo This script deploys an already-published build - it does not run
    echo "dotnet publish" itself. Publish the app first, e.g.:
    echo   dotnet publish src\Keryhe.Telemetry.Server -c Release -r win-x64 --self-contained false -o "%SOURCE_DIR%"
    exit /b 1
)

rem ---------------------------------------------------------------------
rem 3. Detect whether the service is already installed
rem ---------------------------------------------------------------------
set "SERVICE_EXISTS=0"
sc query "%SERVICE_NAME%" >nul 2>&1
if "%errorlevel%"=="0" set "SERVICE_EXISTS=1"

rem ---------------------------------------------------------------------
rem 4. If it exists, stop it before touching its files (update path)
rem ---------------------------------------------------------------------
if "%SERVICE_EXISTS%"=="1" (
    echo Service already installed - stopping it before deploying the update...
    sc stop "%SERVICE_NAME%" >nul 2>&1

    set "STOPPED=0"
    for /l %%i in (1,1,30) do (
        if "!STOPPED!"=="0" (
            sc query "%SERVICE_NAME%" | findstr /i "STATE" | findstr /i "STOPPED" >nul
            if !errorlevel!==0 (
                set "STOPPED=1"
            ) else (
                timeout /t 1 >nul
            )
        )
    )

    if "!STOPPED!"=="0" (
        echo ERROR: Timed out waiting for %SERVICE_NAME% to stop.
        echo Stop it manually ^(services.msc^) and re-run this script.
        exit /b 1
    )
    echo Service stopped.
)

rem ---------------------------------------------------------------------
rem 5. Sync the published files into the install directory
rem ---------------------------------------------------------------------
echo Deploying files to "%INSTALL_DIR%"...
robocopy "%SOURCE_DIR%" "%INSTALL_DIR%" /MIR /R:3 /W:2 >nul
set "ROBOCOPY_RC=%errorlevel%"
if %ROBOCOPY_RC% geq 8 (
    echo ERROR: robocopy failed while deploying files ^(exit code %ROBOCOPY_RC%^).
    exit /b 1
)
echo Files deployed.

rem ---------------------------------------------------------------------
rem 6. First-time install: create the service and configure recovery
rem ---------------------------------------------------------------------
if "%SERVICE_EXISTS%"=="0" (
    echo Creating service %SERVICE_NAME%...
    sc create "%SERVICE_NAME%" binPath= "\"%EXE_PATH%\"" start= auto DisplayName= "%DISPLAY_NAME%" obj= LocalSystem
    if not "!errorlevel!"=="0" (
        echo ERROR: sc create failed.
        exit /b 1
    )

    sc description "%SERVICE_NAME%" "Keryhe Telemetry - OTLP ingestion, REST API, and UI (all-in-one host)."

    rem Restart automatically on failure: 5s after 1st/2nd/3rd crash,
    rem reset the failure count after 24h of continuous uptime.
    sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/5000/restart/5000
    rem Apply recovery actions even on non-crash ("expected") stops too.
    sc failureflag "%SERVICE_NAME%" 1

    echo Service created and configured to start automatically with Windows.
)

rem ---------------------------------------------------------------------
rem 7. Start the service (fresh install's first start, or update's restart)
rem ---------------------------------------------------------------------
echo Starting service...
sc start "%SERVICE_NAME%"
timeout /t 2 >nul
sc query "%SERVICE_NAME%"

echo.
echo ============================================================
echo  Done. Keryhe Telemetry Server listens on:
echo    gRPC (OTLP ingestion) : http://localhost:5117  (h2c)
echo                             https://localhost:7057 (HTTP/2)
echo    REST API + UI         : http://localhost:5188
echo                             https://localhost:7105
echo  Firewall rules and TLS certificates, if needed, are not
echo  configured by this script.
echo ============================================================

endlocal
