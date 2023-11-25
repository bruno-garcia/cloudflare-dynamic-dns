using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Sentry;
using Sentry.Profiling;
using static System.Console;

// To enable Sentry, provide the Sentry DSN via environment variable: SENTRY_DSN
SentrySdk.Init(o =>
{
    o.AttachStacktrace = true; // Event CaptureMessage includes a stacktrace
    o.SendDefaultPii = true;
    o.AutoSessionTracking = true;
    o.TracesSampleRate = 1.0; // Capture transactions and spans
    o.AddExceptionFilterForType<TaskCanceledException>();
    o.Debug = Environment.GetEnvironmentVariable("SENTRY_DEBUG") != null;
    // TODO: will be added once setting ProfilesSampleRate
    o.AddIntegration(new ProfilingIntegration());
});

// Sentry Alert will be set to trigger if the following transaction isn't coming through at the expected rate.
// Example: every hour or so, depending how the cron job running this process is setup.
var transaction = SentrySdk.StartTransaction("dynamic-dns", "dns-update");

SentrySdk.ConfigureScope(s => s.Transaction = transaction);
try
{
    // TODO: Properly validate expected input
    var zoneId = args[0];
    var record = args[1];
    var auth = args[2];
    SentrySdk.ConfigureScope(s =>
    {
        s.SetTag("record", record);
        s.SetTag("zoneId", zoneId);
        // TODO: Validate if auth got here empty or broken (less 3 or less chars)
        s.SetTag("truncated.auth", auth[..3]);
    });

    var tokenSource = new CancellationTokenSource();
    CancelKeyPress += (sender, args) =>
    {
        SentrySdk.AddBreadcrumb("ctrl-c received.");
        args.Cancel = true;
        WriteLine("CTRL+C");
        tokenSource.Cancel(true);
    };

    // TODO: Configurable:
    var serviceUrls = new []
    {
        "https://checkip.amazonaws.com",
        "https://api.ipify.org",
        "https://api.my-ip.io/ip",
    };

    var updateDnsSpan = transaction.StartChild("update.dns");
    try
    {
        // spans from this integration end up picking up the wrong parent "last active span"
        // and showing requests as chained when in fact they are parallelized
        // using var client = new HttpClient(new SentryHttpMessageHandler());
        using var client = new HttpClient();

        // Get cloudflare record id in case the record needs to be updated (this takes the longest so kicking off first)
        var recordIdTask = GetCloudflareRecordId(client, updateDnsSpan, zoneId, record, auth, tokenSource.Token);

        var resolveSpan = updateDnsSpan.StartChild("dns.resolve", $"Resolve '{record}'");
        var ipAddressTask = GetIpAddress(client, updateDnsSpan, serviceUrls, tokenSource.Token);
        var dnsResolve = Dns.GetHostAddressesAsync(record).ContinueWith(c =>
        {
            if (!c.IsFaulted)
            {
                resolveSpan.Description += $" IP: {string.Join(", ", c.Result.Select(i => i.ToString()))}";
            }
            resolveSpan.Finish(c.ToSpanStatus());
            return c.Result;
        });

        var awaitTask = updateDnsSpan.StartChild("task.await", 
            $"Awaiting {nameof(ipAddressTask)} and {nameof(dnsResolve)}");
        try
        {
            await Task.WhenAll(ipAddressTask, dnsResolve);
        }
        finally
        {
            awaitTask.Finish();
        }
        var ipAddress = ipAddressTask.Result;
        transaction.SetTag("ip_address", ipAddress);
        var currentRecordIpAddress = dnsResolve.Result;
        var updated = false;
        if (currentRecordIpAddress.Any(r => ipAddress == r.ToString()))
        {
            tokenSource.Cancel();
            SentrySdk.AddBreadcrumb("Record already pointing to requested IP address. Nothing to do.");
            WriteLine($"Already up-to-date: {ipAddress}");
        }
        else
        {
            var recordIdTaskSpan = updateDnsSpan.StartChild("record.id.await",
                $"Awaiting {nameof(recordIdTask)}");
            try
            {
                await recordIdTask;
            }
            finally
            {
                recordIdTaskSpan.Finish();
            }

            var recordId = recordIdTask.Result;
            await UpdateDnsRecord(client, updateDnsSpan, ipAddress, zoneId, record, recordId, auth,
                tokenSource.Token);
            WriteLine($"Updated to: {ipAddress}");
            SentrySdk.AddBreadcrumb("Successfully updated public IP address.");
            updated = true;
        }
        SentrySdk.ConfigureScope(s =>
        {
            s.SetTag("resolved-ip-address", ipAddress);
            // To get distribution of when it was updated and when it was up-to-date 
            s.SetTag("record-updated", updated.ToString());
            s.SetTag("record-up-to-date", "true");
        });
    }
    catch (AllServicesFailedException asfe)
    {
        var failure = new SentryEvent(
            new Exception($"All {serviceUrls.Length} failed to resolve", asfe))
            {
                Level = SentryLevel.Fatal,
                Fingerprint = new[] {"all-services-failed"}
            };
        SentrySdk.CaptureEvent(failure);
    }
    catch (Exception e)
    {
        SentrySdk.CaptureEvent(new SentryEvent(e) {Level = SentryLevel.Fatal});
        transaction.Status = SpanStatus.InternalError;
        throw;
    }
    finally
    {
        tokenSource.Dispose();
        updateDnsSpan.Finish();
        transaction.Finish();
    }
}
finally
{
    // Wait for events to be sent.
    await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));
}

static async Task<string> GetCloudflareRecordId(
    HttpMessageInvoker client,
    ISpan parent,
    string zoneId, string record, string auth,
    CancellationToken token)
{
    var span = parent.StartChild("record.id.http", $"Retrieve record id for {record} from Cloudflare");
    try
    {
        var url = $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records?type=A&name={record}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth);
        var response = await client.SendAsync(request, token);

        var json = await response.Content.ReadFromJsonAsync<CloudflareResponse>(cancellationToken: token);
        var id = json?.result?[0].id;
        if (id is null)
        {
            throw new Exception($"Expected zone id but received code '{response.StatusCode}'" +
                                $" and body:\n{await response.Content.ReadAsStringAsync(token)}");
        }

        span.Finish();
        return id;
    }
    catch (Exception e)
    {
        span.Finish(e);
        throw;
    }
}

static async Task UpdateDnsRecord(
    HttpMessageInvoker client,
    ISpan parent,
    string ipAddress,
    string zoneId,
    string record,
    string recordId,
    string auth,
    CancellationToken token)
{
    var span = parent.StartChild("update.dns.record", $"Update cloudflare record: '{record}'");
    var spanStatus = SpanStatus.Ok;
    try
    {
        var url = $"https://api.cloudflare.com/client/v4/zones/{zoneId}/dns_records/{recordId}";
        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // request.Headers.Add("Content-Type", "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth);
        var payload =
            $"{{\"type\":\"A\",\"name\":\"{record}\",\"content\":\"{ipAddress}\",\"ttl\":1,\"proxied\":false}}";
        request.Content =
            new StringContent(
                payload,
                Encoding.UTF8,
                "application/json");

        var response = await client.SendAsync(request, token);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<dynamic>(cancellationToken: token);
            var ex = new Exception($"Cloudflare request failed: status {response.StatusCode}");
            ex.AddSentryContext("http.context", new Dictionary<string, object>
            {
                {"request", payload},
                {"response", body},
            });
            throw ex;
        }
    }
    catch
    {
        // TODO: Expose Sentry's SDK HttpStatus to SpanStatus mapping
        spanStatus = SpanStatus.UnknownError;
        throw;
    }
    finally
    {
        span.Finish(spanStatus);
    }
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
            var service = taskToService[completedTask];
            taskSpan.Description = $"Status {status}";
            taskSpan.Finish(status);
            
            querySpan.Description = $"Processing: {service}";
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
                try
                {
                    serviceResponse = Regex.Replace(serviceResponse, "[^0-9.]", "");
                    var ipAddress = IPAddress.Parse(serviceResponse).ToString();
                    SentrySdk.AddBreadcrumb("Cancelling other HTTP requests since a valid IP address was found.");
                    cancelSource.Cancel(); // Cancel any other get in-flight
                    parseSpan.Description = $"Resolved: {ipAddress}";
                    parseSpan.Finish();
                    return ipAddress;
                }
                catch (Exception e)
                {
                    parseSpan.Finish(e);
                    throw;
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

internal static class TaskExtensions
{
    
    public static SpanStatus ToSpanStatus(this Task task)
        => task.Status switch
        {
            TaskStatus.RanToCompletion => SpanStatus.Ok,
            TaskStatus.Canceled => SpanStatus.Cancelled,
            TaskStatus.Faulted => SpanStatus.InternalError,
            _ => (SpanStatus)(-1) // No plain 'unknown' ?
            // _ => SpanStatus.UnknownError
        };
}

internal class AllServicesFailedException : Exception
{
    
}

internal class CloudflareResponse
{
    public ZoneInfo[]? result { get; set; }
}

internal class ZoneInfo
{
    public string? id { get; set; }
}
