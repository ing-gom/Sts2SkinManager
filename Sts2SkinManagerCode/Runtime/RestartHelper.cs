using System;
using System.Diagnostics;
using System.IO;
using Godot;

namespace Sts2SkinManager.Runtime;

public static class RestartHelper
{
    private const uint Sts2SteamAppId = 2868840;

    public static void TriggerRestart(string managerDataDir)
    {
        try
        {
            var os = OS.GetName();
            if (os == "Windows")
                SpawnWindowsHelper(managerDataDir);
            else
                SpawnPosixHelper(managerDataDir, os);

            // Quit AFTER the watcher is spawned, so it can wait for us to exit then relaunch.
            Callable.From(() =>
            {
                try
                {
                    if (Engine.GetMainLoop() is SceneTree tree)
                        tree.Quit();
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"quit failed: {ex.Message}");
                }
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"auto-restart failed: {ex}");
        }
    }

    private static void SpawnWindowsHelper(string managerDataDir)
    {
        var processName = Process.GetCurrentProcess().ProcessName;
        var exeName = processName + ".exe";
        var helperPath = Path.Combine(managerDataDir, "restart_helper.bat");

        var bat = $@"@echo off
:wait
tasklist /fi ""imagename eq {exeName}"" 2>nul | find /i ""{exeName}"" >nul
if %errorlevel%==0 (
    timeout /t 1 /nobreak >nul
    goto wait
)
start """" steam://run/{Sts2SteamAppId}
timeout /t 2 /nobreak >nul
del ""%~f0""
";
        File.WriteAllText(helperPath, bat);

        Process.Start(new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        });

        MainFile.Logger.Info($"auto-restart helper spawned (watching for {exeName}); quitting STS2");
    }

    // macOS/Linux: a POSIX sh helper that waits for THIS process (by pid) to exit, relaunches via
    // Steam's URL handler (open on macOS, xdg-open on Linux), then deletes itself. Started through
    // /bin/sh so no chmod is needed. If no steam:// handler is registered (rare on Linux) the
    // relaunch simply no-ops — the game still quits cleanly and the user relaunches by hand.
    private static void SpawnPosixHelper(string managerDataDir, string osName)
    {
        var pid = Process.GetCurrentProcess().Id;
        var opener = osName == "macOS" ? "open" : "xdg-open";
        var helperPath = Path.Combine(managerDataDir, "restart_helper.sh");

        var sh = "#!/bin/sh\n"
               + $"while kill -0 {pid} 2>/dev/null; do sleep 1; done\n"
               + $"{opener} \"steam://run/{Sts2SteamAppId}\" >/dev/null 2>&1\n"
               + "rm -- \"$0\"\n";
        File.WriteAllText(helperPath, sh);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(helperPath);
        Process.Start(psi);

        MainFile.Logger.Info($"auto-restart helper spawned ({opener}, pid {pid}); quitting STS2");
    }
}
