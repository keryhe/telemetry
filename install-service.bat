@echo off
setlocal enabledelayedexpansion

rem =====================================================================
rem Installs or updates the two Keryhe Telemetry Windows Services:
rem
rem   KeryheTelemetryCollector  Keryhe.Telemetry.Collector.Server.exe
rem                             (OTLP gRPC ingestion, the write path)
rem   KeryheTelemetryApi        Keryhe.Telemetry.Api.Server.exe
rem                             (REST API, UI, alerting and retention)
rem
rem The collector and the API are separate processes by design (separate
rem security boundaries and scaling), so this script installs both.
rem
rem Usage:
rem   install-service.bat [collector-publish-folder] [api-publish-folder]
rem
rem   Each folder holds that host's published output, e.g. produced by:
rem     dotnet publish src\Keryhe.Telemetry.Collector.Server ^
rem       -c Release -r win-x64 --self-contained false -o publish-collector
rem     dotnet publish src\Keryhe.Telemetry.Api.Server ^
rem       -c Release -r win-x64 --self-contained false -o publish-api
rem   Defaults: "publish-collector" and "publish-api" next to this file.
rem
rem Safe to re-run: an installed service is stopped, its files are synced
rem and it is restarted (update path). A service that is not installed yet
rem is created, set to start with Windows and to auto-restart on failure,
rem then started.
rem
rem The former all-in-one service (KeryheTelemetryServer), if present, is
rem stopped and deleted first: it holds the same ports. Its files in the
rem install root are left in place.
rem
rem Before first start, give each service its configuration (connection
rem strings, the collector's TLS certificate) in its install folder's
rem appsettings.Production.json or in machine environment variables. An
rem update never deletes or overwrites an install folder's
rem appsettings.Production.json (so one in a publish folder is not deployed
rem over an existing install: edit the installed copy instead). The
rem collector refuses to start on a plaintext address outside Development
rem unless Telemetry:Collector:AllowInsecureTransport is true (see
rem src\Keryhe.Telemetry.Collector\README.md).
rem =====================================================================

set "INSTALL_ROOT=C:\Services\KeryheTelemetry"

set "COLLECTOR_SRC=%~1"
if "%COLLECTOR_SRC%"=="" set "COLLECTOR_SRC=%~dp0publish-collector"
set "API_SRC=%~2"
if "%API_SRC%"=="" set "API_SRC=%~dp0publish-api"
rem Strip any trailing backslash so paths below don't end up with "\\"
if "%COLLECTOR_SRC:~-1%"=="\" set "COLLECTOR_SRC=%COLLECTOR_SRC:~0,-1%"
if "%API_SRC:~-1%"=="\" set "API_SRC=%API_SRC:~0,-1%"

echo ============================================================
echo  Keryhe Telemetry - Windows Service install/update
echo ============================================================
echo  Install root  : %INSTALL_ROOT%
echo  Collector src : %COLLECTOR_SRC%
echo  API src       : %API_SRC%
echo ============================================================
echo.

rem Require Administrator privileges (service create/config/copy need it)
net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo ERROR: This script must be run as Administrator.
    echo Right-click install-service.bat and choose "Run as administrator".
    exit /b 1
)

rem Verify both source publish outputs exist before touching anything
if not exist "%COLLECTOR_SRC%\Keryhe.Telemetry.Collector.Server.exe" (
    echo ERROR: "%COLLECTOR_SRC%\Keryhe.Telemetry.Collector.Server.exe" was not found.
    echo This script deploys already-published builds - it does not run "dotnet publish" itself:
    echo   dotnet publish src\Keryhe.Telemetry.Collector.Server -c Release -r win-x64 --self-contained false -o "%COLLECTOR_SRC%"
    exit /b 1
)
if not exist "%API_SRC%\Keryhe.Telemetry.Api.Server.exe" (
    echo ERROR: "%API_SRC%\Keryhe.Telemetry.Api.Server.exe" was not found.
    echo   dotnet publish src\Keryhe.Telemetry.Api.Server -c Release -r win-x64 --self-contained false -o "%API_SRC%"
    exit /b 1
)

rem Remove the former all-in-one service: it binds the same ports (7057, 5188, 7105)
set "OLD_SERVICE=KeryheTelemetryServer"
sc query "%OLD_SERVICE%" >nul 2>&1
if "%errorlevel%"=="0" (
    echo --- %OLD_SERVICE% ^(former all-in-one host^) ---
    echo Removing the former all-in-one service, which uses the same ports...
    call :stop_service "%OLD_SERVICE%"
    if not "!errorlevel!"=="0" exit /b 1
    sc delete "%OLD_SERVICE%" >nul
    if not "!errorlevel!"=="0" (
        echo ERROR: sc delete %OLD_SERVICE% failed.
        exit /b 1
    )
    echo %OLD_SERVICE% deleted. Its files in "%INSTALL_ROOT%" were left in place: copy any
    echo settings you need from its appsettings*.json into Collector\ and Api\ as
    echo appsettings.Production.json.
    echo.
)

call :install_service "KeryheTelemetryCollector" "Keryhe Telemetry Collector" "%INSTALL_ROOT%\Collector" "Keryhe.Telemetry.Collector.Server.exe" "%COLLECTOR_SRC%" "Keryhe Telemetry - OTLP gRPC ingestion (collector)."
if not "%errorlevel%"=="0" exit /b 1

call :install_service "KeryheTelemetryApi" "Keryhe Telemetry API" "%INSTALL_ROOT%\Api" "Keryhe.Telemetry.Api.Server.exe" "%API_SRC%" "Keryhe Telemetry - REST API, UI, alerting and retention."
if not "%errorlevel%"=="0" exit /b 1

echo.
echo ============================================================
echo  Done. Default ports (see each host's appsettings.json):
echo    Collector, gRPC OTLP ingestion : https://0.0.0.0:7057 (HTTP/2, needs a certificate)
echo    API + UI                       : http://localhost:5188
echo                                     https://localhost:7105
echo  Firewall rules and TLS certificates are not configured by this script.
echo ============================================================

endlocal
exit /b 0

rem ---------------------------------------------------------------------
rem :install_service <name> <display> <install-dir> <exe> <source-dir> <description>
rem ---------------------------------------------------------------------
:install_service
setlocal enabledelayedexpansion
set "SERVICE_NAME=%~1"
set "DISPLAY_NAME=%~2"
set "INSTALL_DIR=%~3"
set "EXE_PATH=%~3\%~4"
set "SOURCE_DIR=%~5"
set "DESCRIPTION=%~6"

echo --- %SERVICE_NAME% ---

set "SERVICE_EXISTS=0"
sc query "%SERVICE_NAME%" >nul 2>&1
if "%errorlevel%"=="0" set "SERVICE_EXISTS=1"

rem If it exists, stop it before touching its files (update path)
if "%SERVICE_EXISTS%"=="1" (
    echo Service already installed - stopping it before deploying the update...
    call :stop_service "%SERVICE_NAME%"
    if not "!errorlevel!"=="0" exit /b 1
)

rem Sync the published files into the install directory. /XF keeps the
rem operator's appsettings.Production.json: with /MIR it is excluded from both
rem the copy and the purge, so an update never deletes or overwrites it.
echo Deploying files to "%INSTALL_DIR%"...
robocopy "%SOURCE_DIR%" "%INSTALL_DIR%" /MIR /XF appsettings.Production.json /R:3 /W:2 >nul
set "ROBOCOPY_RC=%errorlevel%"
if %ROBOCOPY_RC% geq 8 (
    echo ERROR: robocopy failed while deploying files ^(exit code %ROBOCOPY_RC%^).
    exit /b 1
)
echo Files deployed.

rem First-time install: create the service and configure recovery
if "%SERVICE_EXISTS%"=="0" (
    echo Creating service %SERVICE_NAME%...
    sc create "%SERVICE_NAME%" binPath= "\"%EXE_PATH%\"" start= auto DisplayName= "%DISPLAY_NAME%" obj= LocalSystem
    if not "!errorlevel!"=="0" (
        echo ERROR: sc create failed.
        exit /b 1
    )

    sc description "%SERVICE_NAME%" "%DESCRIPTION%"

    rem Restart automatically on failure: 5s after 1st/2nd/3rd crash,
    rem reset the failure count after 24h of continuous uptime.
    sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/5000/restart/5000
    rem Apply recovery actions even on non-crash ("expected") stops too.
    sc failureflag "%SERVICE_NAME%" 1

    echo Service created and configured to start automatically with Windows.
)

rem Start the service (fresh install's first start, or update's restart)
echo Starting service...
sc start "%SERVICE_NAME%"
timeout /t 2 >nul
sc query "%SERVICE_NAME%"
echo.

endlocal
exit /b 0

rem ---------------------------------------------------------------------
rem :stop_service <name>  Stops the service and waits up to 30s; exit code 1 on timeout
rem ---------------------------------------------------------------------
:stop_service
setlocal enabledelayedexpansion
set "STOP_NAME=%~1"
sc stop "%STOP_NAME%" >nul 2>&1

set "STOPPED=0"
for /l %%i in (1,1,30) do (
    if "!STOPPED!"=="0" (
        sc query "%STOP_NAME%" | findstr /i "STATE" | findstr /i "STOPPED" >nul
        if !errorlevel!==0 (
            set "STOPPED=1"
        ) else (
            timeout /t 1 >nul
        )
    )
)

if "!STOPPED!"=="0" (
    echo ERROR: Timed out waiting for %STOP_NAME% to stop.
    echo Stop it manually ^(services.msc^) and re-run this script.
    endlocal
    exit /b 1
)
echo %STOP_NAME% stopped.
endlocal
exit /b 0
