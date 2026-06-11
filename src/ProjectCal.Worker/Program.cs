using ProjectCal.Api.Configuration;
using ProjectCal.Api.Data;

namespace ProjectCal.Worker;

public static class WorkerHostProgram
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseProjectCalDatabase(builder.Configuration, "Data Source=projectcal-dev.db");
        });
        builder.Services.AddHttpClient<ISpeechToTextService, SpeechToTextService>();
        builder.Services.AddHostedService<TranscriptionWorker>();

        var host = builder.Build();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DatabaseInitializer.EnsureProjectCalSchemaAsync(db, host.Services.GetRequiredService<IConfiguration>()).GetAwaiter().GetResult();
        }

        host.Run();
    }

}
