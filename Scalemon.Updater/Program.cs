using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scalemon.Updater;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Scalemon.Updater");
builder.Services.AddSingleton<WindowsApplicationService>();
builder.Services.AddSingleton<SignedPackages>();
builder.Services.AddSingleton<UpdateEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateEngine>());
await builder.Build().RunAsync();
