
using System.Text;
using System.Diagnostics;
using Marisa.Backend.NapCat;
using Marisa.Configuration;
using Marisa.Plugin;
using Marisa.Plugin.Shared.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Web;

namespace Marisa.StartUp;

public static class Program
{
    private static async Task Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        // asp dotnet
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.ConfigLogger();
        var config = ConfigurationManager.Configuration;
        foreach (var service in NapCatBackend.Config(Utils.Assembly().GetTypes()))
            builder.Services.Add(service);
        builder.WebHost.UseUrls(config.Web.PrivateBaseUrl);

        // use nLog for logging
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Host.UseNLog();

        var app = builder.Build();
        var botDriver = app.Services.GetRequiredService<BotDriver.BotDriver>();
        var accessLogger = NLog.LogManager.GetLogger("Marisa.StartUp.HttpAccess");
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            botDriver.Stop();
            WebApi.CloseBrowserAsync().GetAwaiter().GetResult();
        });

        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next();
            }
            finally
            {
                accessLogger.Info(
                    "HTTP {0} {1}{2} responded {3} in {4:F1} ms",
                    context.Request.Method,
                    context.Request.PathBase,
                    context.Request.Path,
                    context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        });

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
            {
                context.Response.StatusCode  = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Internal Server Error");
            }));
        }

        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapControllers();

        app.UseCors(c => c.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

        var webRootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
        if (Directory.Exists(webRootPath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(webRootPath),
                RequestPath  = ""
            });
        }

        app.MapGet("/", ctx =>
        {
            ctx.Response.Redirect("/index.html");
            return Task.CompletedTask;
        });
        if (Directory.Exists(webRootPath))
        {
            app.MapFallbackToFile("index.html");
        }

        // run
        await Task.WhenAll(app.RunAsync(), botDriver.Invoke());
    }
}
