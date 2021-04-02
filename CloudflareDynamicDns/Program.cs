using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Sentry;
using static System.Console;

using var _ = SentrySdk.Init(o =>
{
    o.Dsn = "https://963b1a54ba3a48a69378145586f70b65@o117736.ingest.sentry.io/5703176";
    o.AttachStacktrace = true; // Event CaptureMessage includes a stacktrace
    o.TracesSampleRate = 1.0; // Capture transactions and spans
    o.Debug = true;
});
var transaction = SentrySdk.StartTransaction("dynamic-dsn", "dsn-update", "update dsn with public facing ip");
var transactionStatus = SpanStatus.Ok; 
// TODO: Expecting that now everything takes the trace-id 
// var runId = Guid.NewGuid().ToString();
// SentrySdk.ConfigureScope(s => s.SetTag("run-id", runId)); // All events sent will be correlated

try
{
    var client = new HttpClient();

    var amazonSpan = transaction.StartChild("task.start", "starting amazon aws ip check");
    var publicIpAmazonTask = client.GetStringAsync("https://checkip.amazsonaws.com").CompleteTask(amazonSpan);

    var ipifySpan = transaction.StartChild("task.start", "starting ipify ip check");
    var publicIpIpifyTask = client.GetStringAsync("https://api.ipify.org").CompleteTask(ipifySpan);

    var myipSpan = transaction.StartChild("task.start", "starting my-ip.io ip check");
    var publicIpMyIpTask = client.GetStringAsync("https://api.my-ip.io/ip").CompleteTask(myipSpan);

    var allTasks = new[] {publicIpAmazonTask, publicIpIpifyTask, publicIpMyIpTask};

    while (allTasks.Length != 0)
    {
        var firstTask = transaction.StartChild("task.any", "awaiting for the quickest task");
        var task = Task.WaitAny(allTasks);
        SentrySdk.AddBreadcrumb($"Resolved task at the index {task}");
        firstTask.Finish();

        var completedTask = allTasks[task];
        SentrySdk.ConfigureScope(s => s.SetTag("provider", ProviderName(completedTask)));

        try
        {
            var value = await completedTask; // Task should be completed (see Await above) but possibly throws here.
            SentrySdk.AddBreadcrumb($"Resolved IP address {value}");
            WriteLine(value);
            SentrySdk.WithScope(s =>
            {
                s.SetTag("resolved-ip-address", value);
                // Sentry Alert will be set to trigger if the following event isn't coming through at the expected rate.
                // Example: every hour or so, depending how the cron job running this process is setup.
                SentrySdk.WithScope(s =>
                {
                    // Stacktrace is always included, and even if we change the code, lets group everything under this key:
                    s.SetFingerprint("success");
                    SentrySdk.CaptureMessage("Successfully resolved public IP address.");
                });
            });
            break;
        }
        catch (Exception e)
        {
            allTasks = allTasks.Where(t => !ReferenceEquals(completedTask, t)).ToArray();
            if (allTasks.Length == 0)
            {
                SentrySdk.WithScope(s =>
                {
                    s.Level = SentryLevel.Fatal;
                    // s.Extra[""]
                    SentrySdk.CaptureException(e);
                });
                throw;
            }

            SentrySdk.WithScope(s =>
            {
                s.Level = SentryLevel.Warning;
                SentrySdk.CaptureException(e);
            });
            // Useful on the next event, if we have one:
            SentrySdk.AddBreadcrumb($"Failed to get IP address.",
                data: new Dictionary<string, string>
                {
                    {"exception", e.ToString()}
                });
        }
    }

    string ProviderName(Task taskInstance)
    {
        if (taskInstance == publicIpAmazonTask) return "amazon";
        if (taskInstance == publicIpIpifyTask) return "ipify";
        return taskInstance == publicIpMyIpTask ? "my-ip" : "unknown";
    }
}
catch (Exception e)
{
    SentrySdk.WithScope(s =>
    {
        s.Level = SentryLevel.Fatal;
        SentrySdk.CaptureException(e);
    });

    transactionStatus = SpanStatus.InternalError; // Make sure transaction fails
    throw;
}
finally
{
    transaction.Finish(transactionStatus);
    // Wait for events to be sent.
    WriteLine("Flush Transaction");
    SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
}

static class TaskExtension
{
    public static Task<TResult> CompleteTask<TResult>(this Task<TResult> task, ISpan span) =>
        task.ContinueWith(t =>
        {
            span.Finish(t.ToSpanStatus());
            return t.GetAwaiter().GetResult(); // Unwrap Task
        });
}
static class SpanExtension
{
    public static SpanStatus ToSpanStatus(this Task task)
        => task.Status == TaskStatus.RanToCompletion
        // TODO: Assumes called form Completetask: Could consider cancelled, still running or other:
            ? SpanStatus.Ok : SpanStatus.UnknownError;
}