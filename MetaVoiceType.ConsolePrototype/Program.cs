using System.Text;
using CommandLine;
using Microsoft.Extensions.Logging;
using MetaVoiceType.ConsolePrototype;

Console.OutputEncoding = Encoding.UTF8;

static ILoggerFactory CreateLoggerFactory()
{
    return LoggerFactory.Create(b => b
        .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Warning)
        .SetMinimumLevel(LogLevel.Information));
}

var parser = new Parser(s => s.HelpWriter = Console.Error);
var exitCode = await parser.ParseArguments<Options>(args)
    .MapResult(
        async (Options o) =>
        {
            using ILoggerFactory factory = CreateLoggerFactory();
            ILogger log = factory.CreateLogger("MetaVoiceType.Prototype");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            var app = new PrototypeApp(o, log);
            return await app.RunAsync(cts.Token).ConfigureAwait(false);
        },
        errs => Task.FromResult(1));

return exitCode;
