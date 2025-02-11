﻿//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: An example program of how to use the SIPSorcery core library to register a SIP account. 
//
// Author(s):
// Aaron Clauson
//  
// History:
// 07 Oct 2019	Aaron Clauson	Created (aaron@sipsorcery.com), SIP Sorcery PTY LTD, Dublin, Ireland (www.sipsorcery.com).
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Net;
using System.Threading;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using Serilog;
using Microsoft.Extensions.Logging;

namespace SIPSorcery.Register
{
    class Program
    {
        private const int SUCCESS_REGISTRATION_COUNT = 3;   // Number of successful registrations to attempt before exiting process.

        static void Main(string[] args)
        {
            Console.WriteLine("SIPSorcery registration user agent example.");
            Console.WriteLine("Press ctrl-c to exit.");

            // Logging configuration. Can be ommitted if internal SIPSorcery debug and warning messages are not required.
            var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
            var loggerConfig = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();
            loggerFactory.AddSerilog(loggerConfig);
            SIPSorcery.Sys.Log.LoggerFactory = loggerFactory;

            // Set up a default SIP transport.
            var sipTransport = new SIPTransport(SIPDNSManager.ResolveSIPService, new SIPTransactionEngine());
            int port = FreePort.FindNextAvailableUDPPort(SIPConstants.DEFAULT_SIP_PORT);
            var sipChannel = new SIPUDPChannel(new IPEndPoint(LocalIPConfig.GetDefaultIPv4Address(), port));
            sipTransport.AddSIPChannel(sipChannel);

            // Create a client user agent to maintain a periodic registration with a SIP server.
            var regUserAgent = new SIPRegistrationUserAgent(
                sipTransport,
                "softphonesample",
                "password",
                "sipsorcery.com",
                120);

            int successCounter = 0;
            ManualResetEvent taskCompleteMre = new ManualResetEvent(false);

            // Event handlers for the different stages of the registration.
            regUserAgent.RegistrationFailed += (uri, err) => SIPSorcery.Sys.Log.Logger.LogError($"{uri.ToString()}: {err}");
            regUserAgent.RegistrationTemporaryFailure += (uri, msg) => SIPSorcery.Sys.Log.Logger.LogWarning($"{uri.ToString()}: {msg}");
            regUserAgent.RegistrationRemoved += (uri) => SIPSorcery.Sys.Log.Logger.LogError($"{uri.ToString()} registration failed.");
            regUserAgent.RegistrationSuccessful += (uri) =>
            {
                SIPSorcery.Sys.Log.Logger.LogInformation($"{uri.ToString()} registration succeeded.");
                Interlocked.Increment(ref successCounter);
                SIPSorcery.Sys.Log.Logger.LogInformation($"Successful registrations {successCounter} of {SUCCESS_REGISTRATION_COUNT}.");

                if (successCounter == SUCCESS_REGISTRATION_COUNT)
                {
                    taskCompleteMre.Set();
                }
            };

            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                SIPSorcery.Sys.Log.Logger.LogInformation("Exiting...");
                taskCompleteMre.Set();
            };

            // Start the thread to perform the initial registration and then periodically resend it.
            regUserAgent.Start();

            taskCompleteMre.WaitOne();

            regUserAgent.Stop();
            if (sipTransport != null)
            {
                SIPSorcery.Sys.Log.Logger.LogInformation("Shutting down SIP transport...");
                sipTransport.Shutdown();
            }
            SIPSorcery.Net.DNSManager.Stop();
        }
    }
}
