using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RelayProxy.Core;

public static class StartupService
{
    private static string UserName => WindowsIdentity.GetCurrent().Name;
    public static string TaskName => "RelayProxy-" + WindowsIdentity.GetCurrent().User!.Value;
    public static (bool Enabled, string? Executable) Read()
    {
        dynamic? service = null, folder = null, task = null, definition = null, action = null;
        try
        {
            service = CreateService(); folder = service.GetFolder(@"\");
            task = folder.GetTask(TaskName); definition = task.Definition; action = definition.Actions.Item(1);
            return ((bool)task.Enabled, (string)action.Path);
        }
        catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x8004130F) { return (false, null); }
        finally { Release(action); Release(definition); Release(task); Release(folder); Release(service); }
    }
    public static async Task ApplyAsync(bool enabled)
    {
        var state = Read();
        if (state.Enabled == enabled && (!enabled || string.Equals(state.Executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))) return;
        if (EngineRunner.IsAdministrator) { Configure(enabled); return; }
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(enabled ? "--set-startup=on" : "--set-startup=off");
        using var helper = Process.Start(info) ?? throw new InvalidOperationException("Не удалось изменить автозапуск.");
        await helper.WaitForExitAsync();
        if (helper.ExitCode != 0) throw new InvalidOperationException("Не удалось изменить автозапуск Windows. Подробности — в журнале Relay.");
    }
    public static void Configure(bool enabled) => Register(TaskName, enabled, false);
    public static void VerifyRegistration()
    {
        var name = TaskName + "-Verification-" + Guid.NewGuid().ToString("N");
        try
        {
            Register(name, true, true);
            dynamic service = CreateService(); dynamic folder = service.GetFolder(@"\"); dynamic task = folder.GetTask(name);
            dynamic definition = task.Definition; dynamic action = definition.Actions.Item(1); dynamic principal = definition.Principal;
            try
            {
                if ((bool)task.Enabled || (string)action.Path != Environment.ProcessPath || (string)action.Arguments != "--startup" || (int)principal.RunLevel != 1)
                    throw new InvalidOperationException("Startup task validation failed.");
            }
            finally { Release(principal); Release(action); Release(definition); Release(task); Release(folder); Release(service); }
        }
        finally { Register(name, false, true); }
    }
    private static dynamic CreateService()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try { service.Connect(); return service; }
        catch { Release(service); throw; }
    }
    private static void Register(string name, bool enabled, bool disabledTest)
    {
        dynamic? service = null, folder = null, definition = null, registration = null, principal = null, settings = null, triggers = null, trigger = null, actions = null, action = null, task = null;
        try
        {
            service = CreateService(); folder = service.GetFolder(@"\");
            if (!enabled)
            {
                try { folder.DeleteTask(name, 0); }
                catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x8004130F) { }
                return;
            }
            definition = service.NewTask(0);
            registration = definition.RegistrationInfo; registration.Description = "Relay Proxy — запуск при входе пользователя в Windows";
            principal = definition.Principal; principal.UserId = UserName; principal.LogonType = 3; principal.RunLevel = 1;
            settings = definition.Settings; settings.Enabled = !disabledTest; settings.StartWhenAvailable = true;
            settings.DisallowStartIfOnBatteries = false; settings.StopIfGoingOnBatteries = false; settings.ExecutionTimeLimit = "PT0S"; settings.MultipleInstances = 2;
            triggers = definition.Triggers; trigger = triggers.Create(9); trigger.Enabled = true; trigger.UserId = UserName; trigger.Delay = "PT15S";
            actions = definition.Actions; action = actions.Create(0); action.Path = Environment.ProcessPath!; action.Arguments = "--startup"; action.WorkingDirectory = AppContext.BaseDirectory;
            task = folder.RegisterTaskDefinition(name, definition, 6, UserName, null, 3, null);
        }
        finally { Release(task); Release(action); Release(actions); Release(trigger); Release(triggers); Release(settings); Release(principal); Release(registration); Release(definition); Release(folder); Release(service); }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
