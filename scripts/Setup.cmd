@echo off
chcp 65001 >nul
title EDITH - ustanovka
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" %*
if errorlevel 1 pause
