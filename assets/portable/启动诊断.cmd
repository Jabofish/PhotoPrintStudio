@echo off
setlocal
set "PPS_DIAG_ROOT=%~dp0"
pushd "%~dp0"
if not exist "portable_data\logs" mkdir "portable_data\logs"
set "DOTNET_HOST_TRACE=1"
set "DOTNET_HOST_TRACE_VERBOSITY=4"
set "DOTNET_HOST_TRACEFILE=%~dp0portable_data\logs\startup-host.log"
echo Collecting startup information. Please wait about 15 seconds...
powershell.exe -NoProfile -Command "$taskLogs=Join-Path $env:PPS_DIAG_ROOT 'portable_data\logs'; Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture | Format-List; Get-CimInstance Win32_Processor | Select-Object Name | Format-List; $taskProcess=Start-Process -FilePath (Join-Path $env:PPS_DIAG_ROOT 'PhotoPrintStudio.exe') -WorkingDirectory $env:PPS_DIAG_ROOT -PassThru -RedirectStandardOutput (Join-Path $taskLogs 'startup-stdout.log') -RedirectStandardError (Join-Path $taskLogs 'startup-stderr.log'); if($taskProcess.WaitForExit(15000)){ 'ExitCode: '+$taskProcess.ExitCode; 'HexExitCode: 0x{0:X8}' -f ($taskProcess.ExitCode -band 0xffffffffL) }else{ $taskProcess.Refresh(); 'Still running after 15 seconds'; $taskProcess | Select-Object Id,MainWindowHandle,MainWindowTitle,WorkingSet64,CPU | Format-List; $taskProcess.Modules | Select-Object ModuleName,FileName | Format-Table -AutoSize }; Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-10)} -ErrorAction SilentlyContinue | Where-Object {$_.Message -like '*PhotoPrintStudio*'} | Select-Object -First 10 TimeCreated,Id,ProviderName,Message | Format-List" >"portable_data\logs\startup-state.log" 2>&1
echo.
echo Review the logs before sharing. Remove names, private paths and other personal information.
pause
popd
endlocal
