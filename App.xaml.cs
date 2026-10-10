using System;
using System.Diagnostics;
using System.Windows;

namespace ProfileSwitchChrome
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProfileSwitchChrome_crash.log"), ev.ExceptionObject?.ToString() ?? "Unknown error");
                } catch { }
            };

            DispatcherUnhandledException += (s, ev) =>
            {
                try {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProfileSwitchChrome_crash.log"), ev.Exception.ToString());
                } catch { }
            };

            base.OnStartup(e);

            int currentPid = Environment.ProcessId;
            var targetNames = new[] { "AntigravityHub", "ProfileSwitchChrome" };
            foreach (var procName in targetNames)
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    if (p.Id != currentPid)
                    {
                        try
                        {
                            p.Kill();
                            p.WaitForExit(1000);
                        }
                        catch { }
                    }
                }
            }

            try
            {
                var cfg = Services.AppConfig.Load();
                Services.ThemeManager.SetTheme(cfg.ThemeMode);
            }
            catch { }
        }
    }
}
