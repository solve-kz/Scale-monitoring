using AspNetCore.Authentication.Basic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scalemon.ApiService.Controllers;   // ваш namespace контроллеров
using Scalemon.Common;            // ваш namespace с общими классами
using Scalemon.FSM;
using Scalemon.MassaKInterop;
using Scalemon.SerialLink;
using Scalemon.ServiceHost;
using Scalemon.SignalBus;
using Scalemon.SqlDataAccess;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

// Привязываем JSON в POCO
var serviceSettings = new Scalemon.Common.ServiceSettings();
config.Bind(serviceSettings);

// Сразу читаем то, что нужно для Serilog и WebHost
var mainLogPath = serviceSettings.Logging.FilePath.MainLogPath;
var detailedLogPath = serviceSettings.Logging.FilePath.DetailedLogPath;
var apiPort = serviceSettings.Api.Port;
var apiUser = serviceSettings.Authentication.Basic.Username;
var apiPass = serviceSettings.Authentication.Basic.Password;

// Настраиваем Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)
    .WriteTo.File(mainLogPath, rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .WriteTo.File(detailedLogPath, rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
    .CreateLogger();

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .UseSerilog()

    .ConfigureServices((hostContext, services) =>
    {
        // 1) Регистрация IOptions<ServiceSettings>
        services.Configure<ServiceSettings>(config);

        // 2) Фоновые сервисы
        services.AddSingleton<Scalemon.Common.IScaleProcessor>(sp =>
        {
            var system = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.ScaleSettings ;
            var realDriver = new Scalemon.MassaKInterop.ScaleDriver100();
            var driver = new MassaKDriverAdapter(realDriver);
            return new ScaleProcessor(
                sp.GetRequiredService<ILogger<ScaleProcessor>>(),
                driver,
                system .PortName,
                system.StableThreshold,
                system .UnstableThreshold,
                system.PollingIntervalMs
            );
        });

        services.AddSingleton<Scalemon.Common.IDataAccess>(sp =>
        {
            var db = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.DatabaseSettings;
            return new SqlDataAccess(
                sp.GetRequiredService<ILogger<SqlDataAccess>>(),
                db.ConnectionString,
                db.TableName,
                db.MaxRetryQueueSize,
                db.AlarmSize,                               
                sp.GetRequiredService<IHostApplicationLifetime>()
            );
        });

        services.AddSingleton<Scalemon.Common.ISignalBus>(sp =>
        { 
            var plc = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.PlcSettings;
            return new SignalBus(
                sp.GetRequiredService<ILogger<SignalBus>>(),
                plc .PortName,
                plc.BaudRate,
                plc .ReconnectIntervalMs
            );

        });

        services.AddSingleton<Scalemon.Common.IScaleStateMachine>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.SystemSettings  ;
            return new ScaleStateMachine(logger: sp.GetRequiredService<ILogger<ScaleStateMachine>>(),
                minWeight:settings.MinWeight,
                hystWeight: settings.HystWeight,
                semaphoreTimeMs:settings.SemaphoreTimeMs,
                // plus your handlers...
                // Обработчики переходов автомата — отправляем соответствующий код Arduino
                onConnected: () => sp.GetRequiredService<Scalemon.Common.ISignalBus>()
                                          .SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.LinkOn),
                onDisconnected: () => sp.GetRequiredService<Scalemon.Common.ISignalBus>()
                                          .SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.LinkOff),
                onUnstable: () => sp.GetRequiredService<Scalemon.Common.ISignalBus>()
                                          .SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.Unstable),
                onResetToZero: () => sp.GetRequiredService<IScaleProcessor>()
                                          .ResetToZeroAsync(),
                onZeroState: () => sp.GetRequiredService<Scalemon.Common.ISignalBus>()
                                          .SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.Idle),
                onInvalidWeight: () => sp.GetRequiredService<Scalemon.Common.ISignalBus>()
                                          .SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.YellowRedOn),
                onError: () => sp.GetRequiredService<Scalemon.Common.ISignalBus>()
                                          .SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.RedOn),
                onResetAlarm: () => sp.GetRequiredService<Scalemon.Common.ISignalBus>()
                                          .SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.AlarmOff),
                // При успешной записи веса в базу — сигнал «Completed»
                onRecord: async raw =>
                {
                    var dbLogger = sp.GetRequiredService<ILogger<ScalemonService>>();
                    try
                    {
                        await sp.GetRequiredService<IDataAccess>().SaveWeighingAsync(raw);
                        await sp.GetRequiredService<Scalemon.Common.ISignalBus>()
                                .SendAsync(Scalemon.Common.Enums.ArduinoSignalCode.Completed);
                    }
                    catch (Exception ex)
                    {
                        dbLogger.LogError(ex, "Не удалось записать взвешивание");
                    }
                }
            );
        });

        services.AddHostedService<ScalemonService>();

        // 3) Web API
        services.AddControllers()
                .PartManager.ApplicationParts.Add(
                    new Microsoft.AspNetCore.Mvc.ApplicationParts
                        .AssemblyPart(typeof(ServiceApiController).Assembly));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        services.AddAuthentication(BasicDefaults.AuthenticationScheme)
                .AddBasic(opts =>
                {
                    opts.Realm = "Scalemon API";
                    opts.Events = new BasicEvents
                    {
                        OnValidateCredentials = ctx =>
                        {
                            if (ctx.Username == apiUser && ctx.Password == apiPass)
                                ctx.ValidationSucceeded();
                            else
                                ctx.ValidationFailed();
                            return Task.CompletedTask;
                        }
                    };
                });
        services.AddAuthorization();
    })

    .ConfigureWebHostDefaults(web =>
    {
        web.UseKestrel()
           .UseUrls($"http://0.0.0.0:{apiPort}")
           .Configure(app =>
           {
               app.UseSwagger();
               app.UseSwaggerUI(c =>
                   c.SwaggerEndpoint("/swagger/v1/swagger.json", "Scalemon API v1"));

               app.UseRouting();
               app.UseAuthentication();
               app.UseAuthorization();
               app.UseEndpoints(endpoints => endpoints.MapControllers());
           });
    })

    .Build();

host.Run();
