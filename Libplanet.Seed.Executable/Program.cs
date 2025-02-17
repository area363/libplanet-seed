﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Libplanet.Crypto;
using Libplanet.Net;
using Libplanet.Net.Protocols;
using Libplanet.Net.Transports;
using Libplanet.Seed.Executable.Exceptions;
using Libplanet.Seed.Interfaces;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Serilog;
using Serilog.Events;

namespace Libplanet.Seed.Executable
{
    public class Program
    {
#pragma warning disable MEN003 // Method Main must be no longer than 120 lines
        public static async Task Main(string[] args)
        {
            Options options = Options.Parse(args, Console.Error);

            var loggerConfig = new LoggerConfiguration();
            switch (options.LogLevel)
            {
                case "error":
                    loggerConfig = loggerConfig.MinimumLevel.Error();
                    break;

                case "warning":
                    loggerConfig = loggerConfig.MinimumLevel.Warning();
                    break;

                case "information":
                    loggerConfig = loggerConfig.MinimumLevel.Information();
                    break;

                case "debug":
                    loggerConfig = loggerConfig.MinimumLevel.Debug();
                    break;

                case "verbose":
                    loggerConfig = loggerConfig.MinimumLevel.Verbose();
                    break;

                default:
                    loggerConfig = loggerConfig.MinimumLevel.Information();
                    break;
            }

            loggerConfig = loggerConfig
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console();
            Log.Logger = loggerConfig.CreateLogger();

            if (options.IceServer is null && options.Host is null)
            {
                Log.Error(
                    "-h/--host is required if -I/--ice-server is not given."
                );
                Environment.Exit(1);
                return;
            }

            if (!(options.IceServer is null || options.Host is null))
            {
                Log.Warning("-I/--ice-server will not work because -h/--host is given.");
            }

            try
            {
                var privateKey = options.PrivateKey ?? new PrivateKey();
                RoutingTable table = new RoutingTable(privateKey.ToAddress());
                ITransport transport;
                switch (options.TransportType)
                {
                    case "tcp":
                        transport = new TcpTransport(
                            table,
                            privateKey,
                            AppProtocolVersion.FromToken(options.AppProtocolVersionToken),
                            null,
                            host: options.Host,
                            listenPort: options.Port,
                            iceServers: new[] { options.IceServer },
                            differentAppProtocolVersionEncountered: null,
                            minimumBroadcastTarget: options.MinimumBroadcastTarget);
                        break;
                    case "netmq":
                        transport = new NetMQTransport(
                            table,
                            privateKey,
                            AppProtocolVersion.FromToken(options.AppProtocolVersionToken),
                            null,
                            workers: options.Workers,
                            host: options.Host,
                            listenPort: options.Port,
                            iceServers: new[] { options.IceServer },
                            differentAppProtocolVersionEncountered: null,
                            minimumBroadcastTarget: options.MinimumBroadcastTarget);
                        break;
                    default:
                        Log.Error(
                            "-t/--transport-type must be either \"tcp\" or \"netmq\".");
                        Environment.Exit(1);
                        return;
                }

                KademliaProtocol peerDiscovery = new KademliaProtocol(
                    table,
                    transport,
                    privateKey.ToAddress());
                Startup.TableSingleton = table;

                IWebHost webHost = WebHost.CreateDefaultBuilder()
                    .UseStartup<SeedStartup<Startup>>()
                    .UseSerilog()
                    .UseUrls($"http://{options.GraphQLHost}:{options.GraphQLPort}/")
                    .Build();

                using (var cts = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        cts.Cancel();
                    };

                    try
                    {
                        var tasks = new List<Task>
                        {
                            webHost.RunAsync(cts.Token),
                            StartTransportAsync(transport, cts.Token),
                            RefreshTableAsync(peerDiscovery, cts.Token),
                            RebuildConnectionAsync(peerDiscovery, cts.Token),
                        };
                        if (!(options.Peers is null) && options.Peers.Any())
                        {
                            tasks.Add(CheckStaticPeersAsync(
                                options.Peers,
                                table,
                                peerDiscovery,
                                cts.Token));
                        }

                        await Task.WhenAll(tasks);
                    }
                    catch (OperationCanceledException)
                    {
                        await transport.StopAsync(TimeSpan.FromSeconds(1));
                    }
                }
            }
            catch (InvalidOptionValueException e)
            {
                string expectedValues = string.Join(", ", e.ExpectedValues);
                Console.Error.WriteLine($"Unexpected value given through '{e.OptionName}'\n"
                                        + $"  given value: {e.OptionValue}\n"
                                        + $"  expected values: {expectedValues}");
            }
        }
#pragma warning restore MEN003 // Method Main must be no longer than 120 lines

        private static async Task StartTransportAsync(
            ITransport transport,
            CancellationToken cancellationToken)
        {
            await transport.StartAsync(cancellationToken);
            Task task = transport.StartAsync(cancellationToken);
            await transport.WaitForRunningAsync();
            await task;
        }

        private static async Task RefreshTableAsync(
            KademliaProtocol protocol,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    await protocol.RefreshTableAsync(TimeSpan.FromSeconds(60), cancellationToken);
                    await protocol.CheckReplacementCacheAsync(cancellationToken);
                }
                catch (OperationCanceledException e)
                {
                    Log.Warning(e, $"{nameof(RefreshTableAsync)}() is cancelled.");
                    throw;
                }
                catch (Exception e)
                {
                    var msg = "Unexpected exception occurred during " +
                              $"{nameof(RefreshTableAsync)}(): {{0}}";
                    Log.Warning(e, msg, e);
                }
            }
        }

        private static async Task RebuildConnectionAsync(
            KademliaProtocol protocol,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
                    await protocol.RebuildConnectionAsync(Kademlia.MaxDepth, cancellationToken);
                }
                catch (OperationCanceledException e)
                {
                    Log.Warning(e, $"{nameof(RebuildConnectionAsync)}() is cancelled.");
                    throw;
                }
                catch (Exception e)
                {
                    var msg = "Unexpected exception occurred during " +
                              $"{nameof(RebuildConnectionAsync)}(): {{0}}";
                    Log.Warning(e, msg, e);
                }
            }
        }

        private static async Task CheckStaticPeersAsync(
            IEnumerable<BoundPeer> peers,
            RoutingTable table,
            KademliaProtocol protocol,
            CancellationToken cancellationToken)
        {
            var boundPeers = peers as BoundPeer[] ?? peers.ToArray();
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    Log.Warning("Checking static peers. {@Peers}", boundPeers);
                    var peersToAdd = boundPeers.Where(peer => !table.Contains(peer)).ToArray();
                    if (peersToAdd.Any())
                    {
                        Log.Warning("Some of peers are not in routing table. {@Peers}", peersToAdd);
                        await protocol.AddPeersAsync(
                            peersToAdd,
                            TimeSpan.FromSeconds(5),
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException e)
                {
                    Log.Warning(e, $"{nameof(CheckStaticPeersAsync)}() is cancelled.");
                    throw;
                }
                catch (Exception e)
                {
                    var msg = "Unexpected exception occurred during " +
                              $"{nameof(CheckStaticPeersAsync)}(): {{0}}";
                    Log.Warning(e, msg, e);
                }
            }
        }

        private class Startup : IContext
        {
            public RoutingTable Table => TableSingleton;

            internal static RoutingTable TableSingleton { get; set; }
        }
    }
}
