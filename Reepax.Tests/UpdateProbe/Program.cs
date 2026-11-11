using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Logging;

VelopackApp.Build().SetArgs(args).SetLogger(new NullVelopackLogger()).SetAutoApplyOnStartup(false).Run();
string? report = Environment.GetEnvironmentVariable("REEPAX_UPDATE_PROBE_REPORT");
if (string.IsNullOrEmpty(report)) return 2;
string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
File.WriteAllText(report + ".tmp", JsonSerializer.Serialize(new { Version = version, Arguments = args }));
File.Move(report + ".tmp", report, overwrite: true);
return 0;
