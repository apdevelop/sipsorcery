// ============================================================================
// FileName: DNSManager.cs
//
// Description:
// Manages DNS lookups in a non-blocking way.
//
// Author(s):
// Aaron Clauson
//
// History:
// 19 Oct 2007	Aaron Clauson	Created, (aaron@sipsorcery.com), SIP Sorcery PTY LTD, Dublin, Ireland (www.sipsorcery.com).
// 14 Oct 2019  Aaron Clauson   Updatyes after synchronsing DNS classes with source from https://www.codeproject.com/Articles/23673/DNS-NET-Resolver-C.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Text.RegularExpressions;
using SIPSorcery.Sys;
using Heijden.DNS;
using Microsoft.Extensions.Logging;

namespace SIPSorcery.Net
{
    public class DNSManager
    {
        class LookupRequest
        {
            public static LookupRequest Empty = new LookupRequest(null, QType.NULL, DEFAULT_DNS_TIMEOUT, null, null);

            public string Hostname;
            public QType QueryType;
            public int Timeout;
            public List<IPEndPoint> DNSServers;
            public ManualResetEvent CompleteEvent;
            public List<LookupRequest> Duplicates;      // if any DNS lookup requests arrive with the same query they will be stored with the queued query.

            public LookupRequest(string hostname, QType queryType, int timeout, List<IPEndPoint> dnsServers, ManualResetEvent completeEvent)
            {
                Hostname = hostname;
                QueryType = queryType;
                Timeout = timeout;
                DNSServers = dnsServers;
                CompleteEvent = completeEvent;
            }
        }

        private const int NUMBER_LOOKUP_THREADS = 5;            // Number of threads that will be available to undertake DNS lookups.
        private const string LOOKUP_THREAD_NAME = "dnslookup";
        private const int DEFAULT_DNS_TIMEOUT = 5;              // Default timeout in seconds for DNS lookups.
        private const int DEFAULT_A_RECORD_DNS_TIMEOUT = 15;   // Default timeout in seconds for A record DNS lookups.

        private static ILogger logger = Log.Logger;

        private static Queue<LookupRequest> m_queuedLookups = new Queue<LookupRequest>();    // Used to store queued lookups.
        private static List<LookupRequest> m_inProgressLookups = new List<LookupRequest>(); // Used to store lookup requests both that are queued and that are in progress.
        private static AutoResetEvent m_lookupARE = new AutoResetEvent(false);               // Used to trigger next waiting thread to do a queued lookup.

        private static Resolver m_resolver = null;

        private static bool m_close = false;    // Used to shutdown the DNS manager.

        static DNSManager()
        {
            try
            {
                IPEndPoint[] osDNSServers = Resolver.GetDnsServers();
                if (osDNSServers != null && osDNSServers.Length > 0)
                {
                    logger.LogDebug("Initialising DNS resolver with operating system DNS server entries.");

                    osDNSServers.ToList().ForEach(x => logger.LogDebug($"DNS server {x.Address}:{x.Port}"));

                    m_resolver = new Resolver(osDNSServers);
                }
                else
                {
                    logger.LogDebug("Initialising DNS resolver with OpenDNS server entries.");
                    m_resolver = new Resolver(Resolver.DefaultDnsServers.ToArray());
                }

                for (int index = 0; index < NUMBER_LOOKUP_THREADS; index++)
                {
                    Thread lookupThread = new Thread(new ThreadStart(ProcessLookups));
                    lookupThread.Name = LOOKUP_THREAD_NAME + "-" + index.ToString();
                    lookupThread.Start();
                }
            }
            catch (Exception excp)
            {
                logger.LogError("Exception DNSManager (static ctor). " + excp);
            }
        }

        public static void SetDNSServers(List<IPEndPoint> dnsServers)
        {
            if (dnsServers != null && dnsServers.Count > 0)
            {
                m_resolver = new Resolver(dnsServers.ToArray());
            }
        }

        /// <summary>
        /// Does a lookup on the DNSManager's currently cached entries. If not found it returns null indicating no information is currently available on the 
        /// host while at the same time queueing a lookup for the DNSManager to do a lookup. Once the lookup has been completed the entry will be stored in
        /// cache and available for subsequent lookup requests. 
        /// 
        /// This approach to lookups is very useful for some SIP request situations. SIP has a built in retransmit mechanism so rather than hold up the processing
        /// of a SIP request while a DNS lookup is done the request can be ignored and in the time it takes for the SIP request retransmit the lookup can be done.
        /// </summary>
        /// <param name="hostname">The hostname of the A record to lookup in DNS.</param>
        /// <returns>If null is returned it means this is the first lookup for this hostname. The caller should wait a few seconds and call the method again.</returns>
        public static DNSResponse LookupAsync(string hostname)
        {
            return Lookup(hostname, QType.A, DEFAULT_A_RECORD_DNS_TIMEOUT, null, true, true);
        }

        public static DNSResponse LookupAsync(string hostname, QType queryType)
        {
            int lookupTimeout = (queryType == QType.A || queryType == QType.AAAA) ? DEFAULT_A_RECORD_DNS_TIMEOUT : DEFAULT_DNS_TIMEOUT;
            return Lookup(hostname, queryType, lookupTimeout, null, true, true);
        }

        /// <summary>
        /// This method will wait until either the lookup completes or the timeout is reached before returning.
        /// </summary>
        /// <param name="hostname">The hostname of the A record to lookup in DNS.</param>
        /// <param name="timeout">Timeout in seconds for the lookup.</param>
        /// <returns></returns>
        public static DNSResponse Lookup(string hostname, QType queryType, int timeout, List<IPEndPoint> dnsServers)
        {
            return Lookup(hostname, queryType, timeout, dnsServers, true, false);
        }

        public static DNSResponse Lookup(string hostname, QType queryType, int timeout, List<IPEndPoint> dnsServers, bool useCache, bool async)
        {
            if (hostname == null || hostname.Trim().Length == 0)
            {
                return null;
            }

            DNSResponse ipAddressResult = MatchIPAddress(hostname);

            if (ipAddressResult != null)
            {
                return ipAddressResult;
            }
            else if (useCache)
            {
                DNSResponse cacheResult = m_resolver.QueryCache(hostname.Trim().ToLower(), queryType);
                if (cacheResult != null)
                {
                    return cacheResult;
                }
            }

            if (async)
            {
                //logger.LogDebug("DNS lookup cache miss for async lookup to " + queryType.ToString() + " " + hostname + ".");
                QueueLookup(new LookupRequest(hostname.Trim().ToLower(), queryType, timeout, dnsServers, null));
                return null;
            }
            else
            {
                ManualResetEvent completeEvent = new ManualResetEvent(false);
                QueueLookup(new LookupRequest(hostname.Trim().ToLower(), queryType, timeout, dnsServers, completeEvent));

                if (completeEvent.WaitOne(timeout * 1000 * 2, false))
                {
                    //logger.LogDebug("Complete event fired for DNS lookup on " + queryType.ToString() + " " + hostname + ".");
                    // Completed event was fired, the DNS entry will now be in cache.
                    DNSResponse result = m_resolver.QueryCache(hostname, queryType);
                    if (result != null)
                    {
                        return result;
                    }
                    else
                    {
                        //logger.LogDebug("DNS lookup cache miss for " + queryType.ToString() + " " + hostname + ".");
                        // Timeout.
                        DNSResponse timeoutResponse = new DNSResponse();
                        timeoutResponse.Timedout = true;
                        return timeoutResponse;
                    }
                }
                else
                {
                    // If this block gets called it's because the DNS resolver class did not return within twice the timeout period it
                    // was asked to do so in. If this happens a lot further investigation into the DNS resolver class is warranted.
                    logger.LogError("DNSManager timed out waiting for the DNS resolver to complete the lookup for " + queryType.ToString() + " " + hostname + ".");

                    // Timeout.
                    DNSResponse timeoutResponse = new DNSResponse();
                    timeoutResponse.Timedout = true;
                    return timeoutResponse;
                }
            }
        }

        private static DNSResponse MatchIPAddress(string hostname)
        {
            try
            {
                if (hostname != null && hostname.Trim().Length > 0)
                {
                    hostname = hostname.Trim();

                    if (Regex.Match(hostname, @"(\d+\.){3}\d+(:\d+$|$)").Success)
                    {
                        string ipAddress = Regex.Match(hostname, @"(?<ipaddress>(\d+\.){3}\d+)(:\d+$|$)").Result("${ipaddress}");
                        DNSResponse result = new DNSResponse(IPAddress.Parse(ipAddress));
                        return result;
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }
            catch (Exception excp)
            {
                logger.LogError("Exception DNSManager MatchIPAddress. " + excp);
                return null;
            }
        }

        public static void Stop()
        {
            logger.LogDebug("DNSManager Stopping.");

            m_close = true;

            for (int index = 0; index < NUMBER_LOOKUP_THREADS; index++)
            {
                m_lookupARE.Set();
            }
        }

        private static void QueueLookup(LookupRequest lookupRequest)
        {
            lock (m_inProgressLookups)
            {
                LookupRequest inProgressLookup = (from lookup in m_inProgressLookups where lookup.QueryType.ToString() == lookupRequest.QueryType.ToString() && lookup.Hostname == lookupRequest.Hostname select lookup).FirstOrDefault();
                if (inProgressLookup == null)
                {
                    m_inProgressLookups.Add(lookupRequest);

                    lock (m_queuedLookups)
                    {
                        m_queuedLookups.Enqueue(lookupRequest);
                    }

                    logger.LogDebug("DNSManager lookup queued for " + lookupRequest.QueryType + " " + lookupRequest.Hostname + ", queue size=" + m_queuedLookups.Count + ", in progress=" + m_queuedLookups.Count + ".");
                    m_lookupARE.Set();
                }
                else
                {
                    if (lookupRequest.CompleteEvent != null)
                    {
                        lock (m_queuedLookups)
                        {
                            if (inProgressLookup.Duplicates == null)
                            {
                                inProgressLookup.Duplicates = new List<LookupRequest>() { lookupRequest };
                            }
                            else
                            {
                                inProgressLookup.Duplicates.Add(lookupRequest);
                            }
                        }

                        logger.LogDebug("DNSManager duplicate lookup added for " + lookupRequest.QueryType + " " + lookupRequest.Hostname + ", queue size=" + m_queuedLookups.Count + ", in progress=" + m_queuedLookups.Count + ".");
                    }
                }
            }
        }

        private static void ProcessLookups()
        {
            string hostname = null;

            try
            {
                string threadName = Thread.CurrentThread.Name;
                //logger.LogDebug("DNS Lookup Thread " + threadName + " started.");

                while (!m_close)
                {
                    int lookups = 0;
                    while (m_queuedLookups.Count > 0 && !m_close)
                    {
                        LookupRequest lookupRequest = LookupRequest.Empty;
                        string queryType = null;
                        //string hostname = null;
                        DNSResponse dnsResponse = null;
                        DateTime startLookupTime = DateTime.Now;

                        try
                        {
                            lock (m_queuedLookups)
                            {
                                if (m_queuedLookups.Count > 0)
                                {
                                    lookupRequest = m_queuedLookups.Dequeue();
                                    hostname = lookupRequest.Hostname;
                                    queryType = lookupRequest.QueryType.ToString();
                                }
                                else
                                {
                                    // Another thread got in ahead of this one to do the lookup.
                                    continue;
                                }
                            }

                            lookups++;
                            logger.LogDebug("DNSManager thread " + threadName + " looking up " + queryType + " " + lookupRequest.Hostname + ".");

                            if (lookupRequest.DNSServers == null)
                            {
                                dnsResponse = m_resolver.Query(lookupRequest.Hostname, lookupRequest.QueryType, lookupRequest.Timeout);
                            }
                            else
                            {
                                dnsResponse = m_resolver.Query(lookupRequest.Hostname, lookupRequest.QueryType, lookupRequest.Timeout, lookupRequest.DNSServers);
                            }

                            if (dnsResponse == null)
                            {
                                logger.LogWarning("DNSManager resolution error for " + lookupRequest.QueryType + " " + lookupRequest.Hostname + " no response was returned. Time taken=" + DateTime.Now.Subtract(startLookupTime).TotalMilliseconds.ToString("0.##") + "ms.");
                            }
                            else if (dnsResponse.Error != null)
                            {
                                logger.LogWarning("DNSManager resolution error for " + lookupRequest.QueryType + " " + lookupRequest.Hostname + ". " + dnsResponse.Error + ". Time taken=" + DateTime.Now.Subtract(startLookupTime).TotalMilliseconds.ToString("0.##") + "ms.");
                            }
                            else if (lookupRequest.QueryType == QType.A)
                            {
                                if (dnsResponse?.RecordsA.Length > 0)
                                {
                                    logger.LogDebug($"DNSManager resolved A record for {lookupRequest.Hostname} to {dnsResponse.RecordsA[0].Address.ToString()} with TTL {dnsResponse.RecordsA[0].RR.TTL} in {DateTime.Now.Subtract(startLookupTime).TotalMilliseconds.ToString("0.##")}ms.");
                                }
                                else
                                {
                                    logger.LogWarning("DNSManager could not resolve A record for " + lookupRequest.Hostname + " in " + DateTime.Now.Subtract(startLookupTime).TotalMilliseconds.ToString("0.##") + "ms.");
                                }
                            }
                            else if (lookupRequest.QueryType == QType.AAAA)
                            {
                                if (dnsResponse?.RecordsAAAA.Length > 0)
                                {
                                    logger.LogDebug($"DNSManager resolved AAAA record for {lookupRequest.Hostname} to {dnsResponse.RecordsAAAA[0].Address.ToString()} with TTL {dnsResponse.RecordsAAAA[0].RR.TTL} in {DateTime.Now.Subtract(startLookupTime).TotalMilliseconds.ToString("0.##")}ms.");
                                }
                                else
                                {
                                    logger.LogWarning("DNSManager could not resolve AAAA record for " + lookupRequest.Hostname + " in " + DateTime.Now.Subtract(startLookupTime).TotalMilliseconds.ToString("0.##") + "ms.");
                                }
                            }
                            else if (lookupRequest.QueryType == QType.SRV)
                            {
                                logger.LogDebug("DNSManager resolve time for " + lookupRequest.Hostname + " " + lookupRequest.QueryType + " " + DateTime.Now.Subtract(startLookupTime).TotalMilliseconds.ToString("0.##") + "ms.");
                                if (dnsResponse.RecordsRR == null || dnsResponse.RecordsRR.Length == 0)
                                {
                                    logger.LogDebug(" no SRV resource records found for " + lookupRequest.Hostname + ".");
                                }
                                else
                                {
                                    foreach (RecordSRV srvRecord in dnsResponse.RecordSRV)
                                    {
                                        logger.LogDebug(" result: priority=" + srvRecord.PRIORITY + ", weight=" + srvRecord.WEIGHT + ", port=" + srvRecord.PORT + ", target=" + srvRecord.TARGET + ".");
                                    }
                                }
                            }
                            else
                            {
                                logger.LogDebug("DNSManager resolve time for " + lookupRequest.Hostname + " " + lookupRequest.QueryType + " " + DateTime.Now.Subtract(startLookupTime).TotalMilliseconds.ToString("0.##") + "ms.");
                            }
                        }
                        catch (Exception lookupExcp)
                        {
                            //dnsEntry.Unresolvable = true;
                            dnsResponse.Error = "Exception lookup. " + lookupExcp.Message;
                            logger.LogError("Exception DNSManager ProcessLookups Lookup (thread, " + threadName + ", hostname=" + hostname + "). " + lookupExcp.GetType().ToString() + "-" + lookupExcp.Message);
                        }
                        finally
                        {
                            try
                            {
                                if (dnsResponse != null)
                                {
                                    if (lookupRequest.CompleteEvent != null)
                                    {
                                        lookupRequest.CompleteEvent.Set();
                                    }

                                    // Mark any requests for the same hostname complete and fire the completed lookup event where required.
                                    if (lookupRequest.Duplicates != null)
                                    {
                                        foreach (LookupRequest duplicateRequest in lookupRequest.Duplicates)
                                        {
                                            duplicateRequest.CompleteEvent.Set();
                                        }
                                    }
                                }

                                lock (m_inProgressLookups)
                                {
                                    m_inProgressLookups.Remove(lookupRequest);
                                }
                            }
                            catch (Exception excp)
                            {
                                logger.LogError("Exception DNSManager ProcessLookup Adding DNS Response. " + excp.Message);
                            }
                        }
                    }

                    // No more lookups outstanding, put thread to sleep until a new lookup is required.
                    m_lookupARE.WaitOne();
                }

                //logger.LogDebug("Thread " + threadName + " shutdown.");
            }
            catch (Exception excp)
            {
                logger.LogError("Exception DNSManager ProcessLookups. " + excp.Message);
            }
            finally
            {
                if (m_close)
                {
                    m_lookupARE.Set();
                }

                //logger.LogDebug("DNSManager thread shutdown.");
            }
        }
    }
}
