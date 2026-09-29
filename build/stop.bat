@echo off
:: Останавливает движок, лежащий рядом с этим файлом, и только его.
:: Раньше здесь был taskkill по имени процесса — он гасил заодно и sing-box
:: чужих программ (Hiddify, v2rayN и прочих), если те были запущены.
:: Обычный способ остановки — кнопка «Остановить» в Tunor; этот файл на случай,
:: когда программа не запускается.

net session >nul 2>&1
if not %errorLevel% == 0 (
    echo Запрос прав администратора...
    powershell -NoProfile -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -Command "$p = Get-Process sing-box -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq '%~dp0sing-box.exe' }; if ($p) { $p | Stop-Process -Force; 'Движок остановлен.' } else { 'Движок и так не запущен.' }"
