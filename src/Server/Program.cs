// Wiring only. Every decision this file appears to make lives in Hosting/ or Api/.
using Compendio.Hosting;

var app = CompendioHost.Build(args);

if (app is null)
{
    // A CLI verb ran (install, doctor, backup, …) and printed its own output.
    return CompendioCli.ExitCode;
}

await app.RunAsync();
return 0;

/// <summary>Exposed so the integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
