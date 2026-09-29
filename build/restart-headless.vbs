' Перезапускает движок без единого окна: гасит тот sing-box, что лежит рядом с
' этим файлом, и поднимает его заново.
'
' Раньше здесь был taskkill /F /IM sing-box.exe — он убивал все движки на машине,
' включая принадлежащие другим программам. Теперь останавливается ровно один
' процесс, запущенный из этой папки.
'
' Штатный способ — кнопка «Перезапустить» в Tunor: она ведёт лог и следит за
' движком. Этот файл нужен, когда сама программа не открывается.

Option Explicit

Dim FSO, UAC, scriptDir, exePath, killCmd

Set FSO = CreateObject("Scripting.FileSystemObject")
Set UAC = CreateObject("Shell.Application")

scriptDir = FSO.GetParentFolderName(WScript.ScriptFullName)
exePath = scriptDir & "\sing-box.exe"

If Not FSO.FileExists(exePath) Then
    MsgBox "Рядом с этим файлом нет sing-box.exe:" & vbCrLf & exePath, 48, "Tunor"
    WScript.Quit 1
End If

killCmd = "-NoProfile -WindowStyle Hidden -Command " & _
          """Get-Process sing-box -ErrorAction SilentlyContinue | " & _
          "Where-Object { $_.Path -eq '" & exePath & "' } | Stop-Process -Force"""

UAC.ShellExecute "powershell.exe", killCmd, scriptDir, "runas", 0

' Дать порту и TUN-адаптеру освободиться до нового запуска.
WScript.Sleep 1200

UAC.ShellExecute exePath, "run", scriptDir, "runas", 0
