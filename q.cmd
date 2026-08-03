@echo off
rem One entry point for building, launching and driving Quiesce. See scripts\q.ps1 for why each
rem thing it does is necessary.
rem
rem This is a .cmd and not a .ps1 for one reason: a .ps1 cannot be run without an execution-policy
rem argument, so the shortest honest way to launch this app used to be
rem     powershell -ExecutionPolicy Bypass -File .\run-app.ps1
rem which is a lot of ceremony to type between two builds. The Bypass now lives here, once.
rem
rem Usage:  q            build and launch      q inventory   elevated, printed and logged
rem         q stop       stop the running one  q test        run the suite
rem         q help       everything else
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\q.ps1" %*
exit /b %ERRORLEVEL%
