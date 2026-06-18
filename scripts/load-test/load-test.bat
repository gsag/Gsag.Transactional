@echo off
setlocal enabledelayedexpansion

REM Docker compose is in same directory as this script
set "COMPOSE_FILE=%~dp0docker-compose.yml"

if not exist "%COMPOSE_FILE%" (
    echo ERROR: docker-compose.yml not found at %COMPOSE_FILE%
    pause
    exit /b 1
)

REM Start PostgreSQL
echo Starting PostgreSQL container...
docker-compose -f "%COMPOSE_FILE%" up -d
if !errorlevel! neq 0 (
    echo ERROR: Failed to start Docker Compose
    pause
    exit /b 1
)

REM Wait for PostgreSQL to be ready (healthcheck)
echo Waiting for PostgreSQL to be ready...
for /l %%i in (1,1,30) do (
    timeout /t 1 /nobreak >nul
    docker-compose -f "%COMPOSE_FILE%" exec -T postgres pg_isready -U loadtest -d loadtest >nul 2>&1
    if !errorlevel! equ 0 (
        echo PostgreSQL is ready
        goto :run_test
    )
    echo Waiting... %%i/30
)

echo ERROR: PostgreSQL did not become ready in time
docker-compose -f "%COMPOSE_FILE%" down -v
pause
exit /b 1

:run_test
REM Run load test
echo Running load test...
dotnet run --project "%~dp0load-test.csproj"
set TEST_EXITCODE=!errorlevel!

REM Cleanup
echo Cleaning up PostgreSQL container...
docker-compose -f "%COMPOSE_FILE%" down -v

pause
exit /b !TEST_EXITCODE!
