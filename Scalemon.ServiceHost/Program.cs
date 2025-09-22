using AspNetCore.Authentication.Basic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scalemon.ApiService.Controllers;
using Scalemon.Common;
using Scalemon.FSM;
using Scalemon.SerialLink;
using Scalemon.SignalBus;
using Scalemon.SqlDataAccess;
// --- ДОБАВЛЯЕМ USING ДЛЯ BLAZOR ---
// Убедитесь, что namespace соответствует вашему проекту веб-приложения
using Scalemon.WebApp.Components;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Formatting.Json;
using System;
using System.IO;
using System.Threading.Tasks;
// -----------------------------------




// 1) Считываем конфигурацию
var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var settings = config.Get<ServiceSettings>();

// 2) Создаём LevelSwitch, задав начальный уровень из конфига
var levelSwitch = new LoggingLevelSwitch();
var initialLevel = config.GetSection("Logging:Level:Default").Value!;
levelSwitch.MinimumLevel = Enum.Parse<LogEventLevel>(initialLevel, ignoreCase: true);

// Привязываем JSON в POCO
var serviceSettings = new Scalemon.Common.ServiceSettings();
config.Bind(serviceSettings);

// Сразу читаем то, что нужно для Serilog и WebHost
var mainLogPath = serviceSettings.Logging.FilePath.MainLogPath;
var apiPort = serviceSettings.Api.Port;
var apiUser = serviceSettings.Authentication.Basic.Username;
var apiPass = serviceSettings.Authentication.Basic.Password;



// Настраиваем Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)               // ← СНАЧАЛА читаем из appsettings
    .MinimumLevel.ControlledBy(levelSwitch)

    // Меньше системного шума
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Information)
    .MinimumLevel.Override("AspNetCore.Authentication", LogEventLevel.Information) // ← добавили

    // Жёстко вырезаем всю ветку Authentication вне зависимости от уровня
    .Filter.ByExcluding(Matching.FromSource("Microsoft.AspNetCore.Authentication"))
    .Filter.ByExcluding(Matching.FromSource("AspNetCore.Authentication"))          // ← добавили

    // На всякий случай вырежем именно эту фразу на Debug, если вдруг придёт из другой категории
    .Filter.ByExcluding(le => le.Level == LogEventLevel.Debug
        && le.MessageTemplate.Text.Contains("was successfully authenticated", StringComparison.OrdinalIgnoreCase))

    .WriteTo.File(new Serilog.Formatting.Json.JsonFormatter(renderMessage: true),
                  mainLogPath,
                  rollingInterval: RollingInterval.Day)
    .CreateLogger();

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .UseSerilog()
    .ConfigureServices((hostContext, services) =>
    {
        // 1) Регистрация IOptions<ServiceSettings>
        services.Configure<ServiceSettings>(config);
        

        // Регистрируем сам LevelSwitch как singleton, чтобы его можно было обновлять из контроллера
        services.AddSingleton(levelSwitch);


        // 2) Фоновые сервисы (Ваша существующая логика без изменений)
        services.AddSingleton<Scalemon.Common.IScaleProcessor>(sp =>
        {
            var system = sp.GetRequiredService<IOptions<ServiceSettings>>().Value.ScaleSettings;

            var driver = new Scalemon.SerialLink.SerialPortScaleDriver100(
                sp.GetRequiredService<ILogger<Scalemon.SerialLink.SerialPortScaleDriver100>>());

            return new ScaleProcessor(
                sp.GetRequiredService<ILogger<ScaleProcessor>>(),
                driver,
                system.PortName,
                system.StableThreshold,
                system.UnstableThreshold,
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
                plc.PortName,
                plc.BaudRate,
                plc.ReconnectIntervalMs
            );
        });

        services.AddSingleton<IScaleStateMachine>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<PlateauZeroStateMachine>>();

            // возьми ServiceSettings так, как у тебя принято:
            // 1) если ты уже сделал var settings = config.Get<ServiceSettings>(); выше — просто используй его из замыкания
            // ИЛИ
            // 2) через options:
            var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceSettings>>().Value;

            // === СБОРОЧКА cfg (прямо тут, без всяких BuildFsmSettings) ===
            decimal minW = (decimal)settings.SystemSettings.MinWeight;
            decimal hyst = (decimal)settings.SystemSettings.HystWeight;
            int M = Math.Max(1, settings.ScaleSettings.StableThreshold);
            int K = Math.Max(2, settings.ScaleSettings.UnstableThreshold);
            int tareMs = Math.Clamp(settings.SystemSettings.SemaphoreTimeMs, 1500, 5000);

            decimal zeroBand = hyst * 0.2m;
            if (zeroBand < 0.005m) zeroBand = 0.005m;
            if (zeroBand > hyst * 0.5m) zeroBand = hyst * 0.5m;

            var cfg = new PlateauZeroStateMachine.Settings(
                ZeroBandKg: zeroBand,
                ResidualBandKg: hyst,
                NegativeBandKg: hyst,
                MinWeightKg: minW,
                PlateauStableSamples: M,
                ZeroStableSamples: K,
                TareTimeout: TimeSpan.FromMilliseconds(tareMs),
                TareMaxRetries: 2
            );

            if (cfg.MinWeightKg <= cfg.ResidualBandKg)
                log.LogWarning("MinWeight ({min}) ≤ HystWeight ({hyst}). Рассмотри увеличение MinWeight.", cfg.MinWeightKg, cfg.ResidualBandKg);

            // ядро FSM
            var db = sp.GetRequiredService<IDataAccess>();
            var bus = sp.GetRequiredService<ISignalBus>();
            var scale = sp.GetRequiredService<IScaleProcessor>();

            var core = new PlateauZeroStateMachine(
                cfg,
                log,
                bus, // <-- ПЕРЕДАЁМ ШИНУ
                onRecordAsync: async (net) => await db.SaveWeighingAsync(net), // <-- Упрощённый делегат
                sendTare: () => scale.ResetToZeroAsync().GetAwaiter().GetResult()
            );

            return new PlateauZeroFsmAdapter(core, log);
        });

        services.AddHostedService<ScalemonService>();

        // 3) Web API и UI
        services.AddControllers()
            .PartManager.ApplicationParts.Add(
                new Microsoft.AspNetCore.Mvc.ApplicationParts
                    .AssemblyPart(typeof(ServiceApiController).Assembly));
            



        // --- ДОБАВЛЯЕМ СЕРВИСЫ ДЛЯ BLAZOR ---
        services.AddRazorComponents()
                .AddInteractiveServerComponents();
        // ------------------------------------

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

                // --- ДОБАВЛЯЕМ MIDDLEWARE ДЛЯ BLAZOR ---
                // Позволяет использовать статические файлы (CSS, JS) из папки wwwroot
                app.UseStaticFiles();
                // ---------------------------------------

                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseAntiforgery();
                app.UseEndpoints(endpoints =>
                {
                    // Существующая конечная точка для API
                    endpoints.MapControllers().RequireAuthorization();
                    // --- ДОБАВЛЯЕМ КОНЕЧНУЮ ТОЧКУ ДЛЯ BLAZOR ---
                    // App - это корневой компонент вашего WebApp
                    endpoints.MapRazorComponents<App>();
                    // -----------------------------------------
                });
            });
    })
    .Build();

host.Run();
