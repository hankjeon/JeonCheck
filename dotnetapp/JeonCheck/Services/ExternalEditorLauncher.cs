using System.Diagnostics;
using System.IO;

namespace JeonCheck.Services;

/// <summary>
/// 현재 파일을 외부 편집기로 여는 기능.
/// VS Code가 설치되어 있으면 해당 줄로 바로 이동하여 열고(-g 파일:줄), 없으면 메모장으로 연다.
/// </summary>
public static class ExternalEditorLauncher
{
    /// <returns>실행한 편집기 이름 (상태 메시지 표시용)</returns>
    public static string Open(string filePath, int line)
    {
        string? codeExe = FindVsCodeExecutable();
        if (codeExe != null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = codeExe,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add($"{filePath}:{Math.Max(1, line)}");
                Process.Start(psi);
                return "VS Code";
            }
            catch
            {
                // VS Code 실행 실패 시 메모장으로 대체
            }
        }

        var notepad = new ProcessStartInfo
        {
            FileName = "notepad.exe",
            UseShellExecute = false
        };
        notepad.ArgumentList.Add(filePath);
        Process.Start(notepad);
        return "메모장";
    }

    /// <summary>
    /// VS Code 실행 파일(Code.exe) 위치 탐색: PATH에 등록된 'Microsoft VS Code\bin' 폴더 → 사용자/시스템 기본 설치 경로 순
    /// </summary>
    private static string? FindVsCodeExecutable()
    {
        var candidates = new List<string>();

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(Path.Combine(dir, "code.cmd")))
            {
                // ...\Microsoft VS Code\bin\code.cmd → ...\Microsoft VS Code\Code.exe
                string? installDir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(installDir))
                    candidates.Add(Path.Combine(installDir, "Code.exe"));
            }
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var baseDir in new[] { Path.Combine(localAppData, "Programs"), programFiles, programFilesX86 })
        {
            if (!string.IsNullOrEmpty(baseDir))
                candidates.Add(Path.Combine(baseDir, "Microsoft VS Code", "Code.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
