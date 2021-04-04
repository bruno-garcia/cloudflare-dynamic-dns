# Cloudflare Dynamic DNS updater

A tool to update a record on Cloudflare with the public IP address of the running machine.

# Running

To run this, you'll need:

1. The binary in the release or [build from source](#-Build-from-source)
2. The domain you want to change (FQDN record)
3. Cloudflare Zone Id
4. Cloudflare Auth Token
5. Optionally a [Sentry DSN](https://docs.sentry.io/product/sentry-basics/dsn-explainer/) to monitor the process

# Build from source

Install [.NET 5](http://dot.net/)

Build and publish it in release mode. These instructions describe a [self-contained](https://docs.microsoft.com/en-us/dotnet/core/deploying/#publish-self-contained) app so you don't need to install the .NET runtime in the target machine. It's just like a native executable:

`dotnet publish -c release -r osx-x64`

Replace `osx-x64` with the [runtime identifier of the target machine](https://docs.microsoft.com/en-us/dotnet/core/rid-catalog#using-rids) you expect to run this. For example `win-x64`, `linux-x64`, etc.

A single file, compiled to that machine architecture is built.
The build prints out the publish location. It's `bin/release/net5.0/osx-x64/publish/`

## Debugging

Set the environment variable `SENTRY_DEBUG` to `1`. For example (*replace the SENTRY_DSN with your own*):

```sh
SENTRY_DEBUG=1 \
    SENTRY_DSN=https://k@sentry.io/1 \
    dotnet run -- zone_id domain.to.update cloudflare_key`
```

The command above will build the project and run it while also telling the Sentry SDK to be in debug mode.

### Windows

On Windows you want to set the environment variable like:

```batch
set SENTRY_DEBUG=1
set SENTRY_DSN=https://k@sentry.io/1
dotnet run -- zone_id domain.to.update cloudflare_key`
```

# Sentry

This project is instrumented with [Sentry](https://sentry.io). If you provide a Sentry DSN (which you can get for free on sentry.io) it will send telemetry data including crash reports. You can then configure Sentry to send alerts to you by email or other mechanism, to let you know if the job stopped working and detailed information about what went wrong.

To enable Sentry, provide a DSN via environment variable. For example:

`SENTRY_DSN=https://963basdasdaba3a48a69378145586f70b65@o117736.ingest.sentry.io/5703176 SENTRY_DEBUG=1 ./CloudflareDynamicDns`

`SENTRY_DEBUG=1` is optional, but helps when setting things up, to see what the Sentry SDK is doing.