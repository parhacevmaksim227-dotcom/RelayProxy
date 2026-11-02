using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RelayProxy.Core;

// One administrator approval, then silent elevation: an elevated instance registers an on-demand
// scheduled task that runs Relay with the highest privileges. Later launches only start that task,
// so Windows does not ask again. The task lives under the current user and can be removed at any time.
public static class ElevationService
{
    public const string LaunchArgument = "--elevated-launch";
    private static string UserName => WindowsIdentity.GetCurrent().Name;
    public static string TaskName => "RelayProxy-Elevated-" + WindowsIdentity.GetCurrent().User!.Value;
    private static string ArgsFile => Path.Combine(ProfileStore.DirectoryPath, "launch.args");

    // True when the task exists and points at this very executable.
    public static bool IsReady()
    {
        dynamic? service = null, folder = null, task = null, definition = null, actions = null, action = null;
        try
        {
            service = CreateService(); folder = service.GetFolder(@"\");
            task = folder.GetTask(TaskName);
            if (!(bool)task.Enabled) return false;
            definition = task.Definition; actions = definition.Actions; action = actions.Item(1);
            return string.Equals((string)action.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x8004130F) { return false; }
        catch { return false; }
        finally { Release(action); Release(actions); Release(definition); Release(task); Release(folder); Release(service); }
    }

    // Needs administrator rights; called from an elevated instance.
    public static void Configure(bool enabled)
    {
        dynamic? service = null, folder = null, definition = null, registration = null, principal = null, settings = null, actions = null, action = null, task = null;
        try
        {
            service = CreateService(); folder = service.GetFolder(@"\");
            if (!enabled)
            {
                try { folder.DeleteTask(TaskName, 0); }
                catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x8004130F) { }
                return;
            }
            definition = service.NewTask(0);
            registration = definition.RegistrationInfo; registration.Description = "Relay Proxy — запуск с правами администратора без повторного запроса";
            principal = definition.Principal; principal.UserId = UserName; principal.LogonType = 3; principal.RunLevel = 1;
            settings = definition.Settings; settings.Enabled = true; settings.StartWhenAvailable = true; settings.AllowDemandStart = true;
            settings.DisallowStartIfOnBatteries = false; settings.StopIfGoingOnBatteries = false; settings.ExecutionTimeLimit = "PT0S"; settings.MultipleInstances = 2;
            actions = definition.Actions; action = actions.Create(0); action.Path = Environment.ProcessPath!; action.Arguments = LaunchArgument; action.WorkingDirectory = AppContext.BaseDirectory;
            task = folder.RegisterTaskDefinition(TaskName, definition, 6, UserName, null, 3, null);
        }
        finally { Release(task); Release(action); Release(actions); Release(settings); Release(principal); Release(registration); Release(definition); Release(folder); Release(service); }
    }

    // Starts Relay elevated through the task. The arguments travel through a small file because tasks have a fixed command line.
    public static bool TryLaunch(IEnumerable<string> arguments)
    {
        dynamic? service = null, folder = null, task = null, running = null;
        try
        {
            Directory.CreateDirectory(ProfileStore.DirectoryPath);
            File.WriteAllLines(ArgsFile, arguments.Where(a => a != LaunchArgument));
            service = CreateService(); folder = service.GetFolder(@"\"); task = folder.GetTask(TaskName);
            running = task.Run(null);
            return true;
        }
        catch { return false; }
        finally { Release(running); Release(task); Release(folder); Release(service); }
    }

    // The arguments the unelevated instance wanted to pass on; read once by the elevated instance.
    public static string[] TakeArguments()
    {
        try
        {
            if (!File.Exists(ArgsFile)) return [];
            var lines = File.ReadAllLines(ArgsFile).Where(l => l.StartsWith("--", StringComparison.Ordinal)).ToArray();
            File.Delete(ArgsFile); return lines;
        }
        catch { return []; }
    }

    private static dynamic CreateService()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try { service.Connect(); return service; }
        catch { Release(service); throw; }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
