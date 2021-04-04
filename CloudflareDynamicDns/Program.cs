using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Sentry;
using static System.Console;

SentrySdk.Init(o =>
{
    o.Dsn = "https://963b1a54ba3a48a69378145586f70b65@o117736.ingest.sentry.io/5703176";
    o.AttachStacktrace = true; // Event CaptureMessage includes a stacktrace
    o.SendDefaultPii = true;
    o.TracesSampleRate = 1.0; // Capture transactions and spans
    o.Debug = true;
});
var transaction = SentrySdk.StartTransaction("dynamic-dns", "dns-update");
SentrySdk.ConfigureScope(s => s.Transaction = transaction);
try
{
    // TODO: Properly validate expected input
    var zoneId = args[0];
    var record = args[1];
    var auth = args[1];

    var ctrlC = new CancellationTokenSource();
    Console.CancelKeyPress += (sender, args) =>
    {
        args.Cancel = true;
        Console.WriteLine("CTRL+C");
        ctrlC.Cancel(true);
    };

    var transactionStatus = SpanStatus.Ok; 

    var serviceUrls = new []
    {
        "https://checkip.amazonaws.com",
        "https://api.ipify.org",
        "https://api.my-ip.io/ip",
    };

    var updateDnsSpan = transaction.StartChild("update.dns");
    try
    {
        using var client = new HttpClient();
        // spans from this integration end up picking up the wrong parent
        // using var client = new HttpClient(new SentryHttpMessageHandler());

        var ipAddressTask = GetIpAddress(client, updateDnsSpan, serviceUrls, ctrlC.Token);;
        var recordIdTask = GetCloudflareRecordId(client, zoneId, record, auth, ctrlC.Token);
        await Task.WhenAll(ipAddressTask, recordIdTask);
        var ipAddress = ipAddressTask.Result;
        var recordId = recordIdTask.Result;
        transaction.SetTag("ip_address", ipAddress);
        var processIpSpan = transaction.StartChild("process.ip");
        try
        {
            WriteLine(ipAddress);

            var successEvent = new SentryEvent
            {
                // Stacktrace is always included, and even if we change the code, lets group everything under this key:
                Fingerprint = new[] {"success"},
                Level = SentryLevel.Info,
                Message = new SentryMessage {Formatted = $"Successfully resolved public IP address",},
            };
            successEvent.SetTag("resolved-ip-address", ipAddress);

            // Sentry Alert will be set to trigger if the following event isn't coming through at the expected rate.
            // Example: every hour or so, depending how the cron job running this process is setup.
            SentrySdk.CaptureEvent(successEvent);
        }
        finally
        {
            processIpSpan.Finish();
        }
    }
    catch (AllServicesFailedException)
    {
        var failure = new SentryEvent
        {
            Level = SentryLevel.Fatal,
            Message = new SentryMessage{Formatted = $"All {serviceUrls.Length} failed to resolve"},
            Fingerprint = new[] {"all-services-failed"}
        };
        SentrySdk.CaptureEvent(failure);
    }
    catch (Exception e)
    {
        SentrySdk.CaptureEvent(new SentryEvent(e)
        {
            Level = SentryLevel.Fatal
        });

        transactionStatus = SpanStatus.InternalError; // Make sure the transaction fails
        // https://github.com/getsentry/sentry-dotnet/issues/919
        // transaction.Status = SpanStatus.InternalError;
        throw;
    }
    finally
    {
        ctrlC.Dispose();
        updateDnsSpan.Finish();
        transaction.Finish(transactionStatus);
    }
}
finally
{
    // Wait for events to be sent.
    await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
}

static async Task<string> GetCloudflareRecordId(
    HttpClient client,
    string zoneId, string record, string auth,
    CancellationToken token)
{
    var url = $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?type=A&name={record}";
    var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth);
    var response = await client.SendAsync(request, token);

    var json = await response.Content.ReadFromJsonAsync<dynamic>(cancellationToken: token);
    Console.WriteLine(json);
    var id = json?.result?[0].id;
    if (id is null)
    {
        throw new Exception($"Expected zone id but received code '{response.StatusCode}' and body:\n{json}");
    }

    return id;
}

static async Task<string> GetIpAddress(
    HttpClient client, 
    ISpan span,
    IReadOnlyCollection<string> serviceUrls,
    CancellationToken token)
{
    var resolveSpan = span.StartChild("get.ip", "Resolve public IP address");
    var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(token);
    cancelSource.CancelAfter(15000);
    try
    {
        var queries = serviceUrls.AsParallel().Select(url =>
        {
            var httpSpan = resolveSpan.StartChild("http.client", $"GET {url}");
            return client.GetStringAsync(url, cancelSource.Token).ContinueWith(s =>
            {
                // TODO: Make status code mapping public
                httpSpan.Finish(s.ToSpanStatus());
                // Unwrap Result (task has completed):
                return s.Result;
            });
        }).ToArray();

        var taskToService = serviceUrls
            .Zip(queries, (url, task) => (url, task))
            .ToDictionary(k => k.task, v => v.url);
        
        while (queries.Length > 0)
        {
            cancelSource.Token.ThrowIfCancellationRequested();

            var querySpan = resolveSpan.StartChild("query.loop");
            var querySpanStatus = SpanStatus.Ok;

            // Should roughly match the timing of the first HTTP request to complete
            var taskSpan = querySpan.StartChild("task.any");
            var completedTask = await Task.WhenAny(queries);
            var status = completedTask.ToSpanStatus();
            taskSpan.Finish(status);
            var service = taskToService[completedTask];
            querySpan.SetTag("service", service);

            SentrySdk.AddBreadcrumb($"Completed request with '{service}'",
                level: BreadcrumbLevel.Debug, data: new Dictionary<string, string> {{"status", status.ToString()}});
            SentrySdk.ConfigureScope(s => s.SetTag("provider", service));

            string? serviceResponse = null;
            try
            {
                // Task should be completed (see 'await' above) but possibly throws here.
                serviceResponse = await completedTask;
                var parseSpan = querySpan.StartChild("parse.ip");
                var parseSpanStatus = SpanStatus.Ok;
                try
                {
                    serviceResponse = Regex.Replace(serviceResponse, "[^0-9.]", "");
                    var ipAddress = IPAddress.Parse(serviceResponse).ToString();
                    cancelSource.Cancel(); // Cancel any other get in-flight
                    return ipAddress;
                }
                catch
                {
                    parseSpanStatus = SpanStatus.UnknownError;
                    throw;
                }
                finally
                {
                    parseSpan.Finish(parseSpanStatus);
                }
            }
            catch (Exception e)
            {
                var errorSpan = querySpan.StartChild("process.error");
                try
                {
                    var evt = new SentryEvent(e);
                    if (serviceResponse is not null)
                    {
                        evt.SetExtra("response", serviceResponse!);
                    }

                    evt.Message = new SentryMessage();

                    queries = queries.Where(t => !ReferenceEquals(completedTask, t)).ToArray();
                    if (queries.Length == 0)
                    {
                        querySpanStatus = SpanStatus.UnknownError;
                        break;
                    }

                    evt.Level = SentryLevel.Warning;
                    evt.Message.Formatted = $"Failed to resolve IP with {service}. Services left to try: {queries.Length}.";
                    evt.Fingerprint = new[] {"one-of-n-failed"};
                    SentrySdk.CaptureEvent(evt);

                    // Useful on the next event, if we have one:
                    SentrySdk.AddBreadcrumb($"Failed to get IP address.",
                        data: new Dictionary<string, string>
                        {
                            {"ex.message", e.Message},
                            {"ex.trace", e.StackTrace ?? "no available"}
                        },
                        level: BreadcrumbLevel.Warning);
                }
                finally
                {
                    errorSpan.Finish();
                }
            }
            finally
            {
                querySpan.Finish(querySpanStatus);
            }
        }
        throw new AllServicesFailedException();
    }
    finally
    {
        cancelSource.Dispose();
        resolveSpan.Finish();
    }
}

static class SpanExtension
{
    public static SpanStatus ToSpanStatus(this Task task)
        => task.Status switch
        {
            TaskStatus.RanToCompletion => SpanStatus.Ok,
            TaskStatus.Canceled => SpanStatus.Cancelled,
            TaskStatus.Faulted => SpanStatus.InternalError,
            _ => (SpanStatus)(-1) // No 'unknown' ?
            // _ => SpanStatus.UnknownError
        };
}

internal class AllServicesFailedException : Exception
{
    
}