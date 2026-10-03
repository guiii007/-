Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
shell.Run Chr(34) & files.BuildPath(files.GetParentFolderName(WScript.ScriptFullName), "内容迁移.exe") & Chr(34), 0, False
