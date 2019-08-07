using gov.sandia.sld.common.configuration;
using gov.sandia.sld.common.logging;
using gov.sandia.sld.common.utilities;
using gov.sandia.sld.common.requestresponse;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;

// http://stackoverflow.com/a/4042887/706747

namespace gov.sandia.sld.common.data
{
    public class PingResult
    {
        [JsonConverter(typeof(IPAddressConverter))]
        public IPAddress Address { get; set; }
        public bool IsPingable { get; set; }
        public long AvgTime { get; set; }
        public string Name { get; set; }
        public string MAC { get; set; }

        public PingResult()
        {
            Address = null;
            IsPingable = false;
            AvgTime = 0;
            Name = MAC = string.Empty;
        }

        public PingResult(string addr, string name)
        {
            Address = IPAddress.Parse(addr);
            Name = name;

            IsPingable = false;
            AvgTime = 0;
            MAC = string.Empty;
        }

        public bool IsDifferent(PingResult other)
        {
            return Address.Equals(other.Address) == false ||
                IsPingable != other.IsPingable ||
                MAC != other.MAC;
        }

        public bool DoPing(uint repeat = 5)
        {
            bool success = false;
            string addr = Address.ToString();
            ILog log = LogManager.GetLogger(typeof(PingResult));

            try
            {
                if (string.IsNullOrEmpty(Name))
                    log.Debug($"Pinging {addr}");
                else
                    log.Debug($"Pinging {addr} ({Name})");

                long ms = 0;
                uint count = 0;
                int timeout = 1000; // milliseconds
                Stopwatch watch = Stopwatch.StartNew();

                using (Ping pinger = new Ping())
                {
                    for (uint i = 0; i < repeat; ++i)
                    {
                        PingReply reply = pinger.Send(addr, timeout);
                        if (reply.Status == IPStatus.Success)
                        {
                            ++count;
                            ms += reply.RoundtripTime;

                            log.Debug($"Ping {addr} success in {reply.RoundtripTime} ms");
                        }
                        else
                        {
                            ms += timeout;

                            log.Debug($"Ping {addr} failure {reply.Status.ToString()}");
                        }
                    }
                }

                Trace.WriteLine($"Pinging {addr} took {watch.ElapsedMilliseconds} ms");
                log.Debug($"Ping {addr}, count {count}, total ms {ms}");

                // If we average more than half, consider it good
                IsPingable = ((double)count / (double)repeat) >= 0.5f;
                AvgTime = ms / repeat;

                success = true;
            }
            catch (Exception e)
            {
                log.Error($"Error in DoPing: {addr}");
                log.Error(e);
            }

            return success;
        }
    }

    public class IPAddressConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(IPAddress);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            return IPAddress.Parse(JToken.Load(reader).ToString());
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            JToken.FromObject(value.ToString()).WriteTo(writer);
        }
    }

    /// <summary>
    /// Used to ping a series of IP addresses
    /// </summary>
    public class Pinger
    {
        public List<PingResult> Pings { get; private set; }

        /// <summary>
        /// Construct a Pinger with the list of IP addresses/names that should be pinged
        /// </summary>
        /// <param name="ips"></param>
        public Pinger(List<Tuple<string, string>> ips)
        {
            Pings = ips.ConvertAll<PingResult>(ip => new PingResult(ip.Item1, ip.Item2));
        }

        public void DoPings(uint num_pingers)
        {
            // Create some # of threads for concurrent pinging. The number of threads
            // will be around request.NumPingers.
            ManualResetEvent complete_event = new ManualResetEvent(false);
            int thread_count = 0;
            List<Thread> threads = new List<Thread>();
            List<List<PingResult>> chunks = Pings.ChunkBy((int)((uint)Pings.Count / num_pingers));

            foreach (List<PingResult> addrs in chunks)
            {
                // Create the number of threads specified in num_pingers and ping a chunk
                // of addresses in each thread
                Thread t = new Thread(
                    () =>
                    {
                        Stopwatch watch = Stopwatch.StartNew();

                        List<PingResult> local_addrs = addrs;
                        local_addrs.ForEach(a => a.DoPing());

                        Trace.WriteLine($"Pinging {local_addrs.Count} addrs took {watch.ElapsedMilliseconds} ms");

                        if (Interlocked.Decrement(ref thread_count) <= 0)
                            complete_event.Set();
                    });
                threads.Add(t);
            }

            if (threads.Count > 0)
            {
                lock (Pings)
                {
                    try
                    {
                        thread_count = threads.Count;
                        threads.ForEach(t => t.Start());

                        complete_event.WaitOne();
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                    }

                    _mac_addr_retriever.Retrieve();
                    Pings.ForEach(p => _mac_addr_retriever.SetMAC(p));
                }
            }
        }

        private MACAddressRetriever _mac_addr_retriever = new MACAddressRetriever();
    }

    public abstract class ThreadedWorker : IDisposable
    {
        public ThreadedWorker(TimeSpan frequency)
        {
            _frequency = frequency;
        }

        public void Start()
        {
            if (_thread != null || _callback == null)
                return;

            _thread = new Thread(
                () =>
                {
                    bool complete = false;
                    ManualResetEvent[] events = { _stop_event };

                    while (complete == false)
                    {
                        _callback();

                        complete = ManualResetEvent.WaitAny(events, _frequency) == 0;
                    }
                    _stop_complete_event.Set();
                });
            _stop_event.Reset();
            _stop_complete_event.Reset();
            _thread.Start();
        }

        public void Stop()
        {
            _stop_event.Set();
            _stop_complete_event.WaitOne();
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
        }

        protected Action _callback;
        private Thread _thread;
        private TimeSpan _frequency;
        private ManualResetEvent _stop_event = new ManualResetEvent(false);
        private ManualResetEvent _stop_complete_event = new ManualResetEvent(false);
    }

    public class ThreadedPinger : ThreadedWorker
    {
        public Pinger P { get; private set; }

        public ThreadedPinger(List<Tuple<string, string>> ips, uint num_pingers)
            : base(TimeSpan.FromSeconds(10))
        {
            P = new Pinger(ips);
            _num_pingers = num_pingers;
            _callback = OnPing;
        }

        public void OnPing()
        {
            P.DoPings(_num_pingers);
        }

        private uint _num_pingers;
    }

    /// <summary>
    /// The DataCollector that collects ping data for the local network.
    /// </summary>
    public class PingCollector : DataCollector
    {
        public PingCollector(CollectorID id)
            : base(new DataCollectorContext(id, ECollectorType.Ping))
        {
            _pinger = new ThreadedPinger(_last_to_ping, 8);
        }

        public override CollectedData OnAcquire()
        {
            if (_last_to_ping == null || _last_to_ping.Count == 0)
            {
                _last_to_ping = GetToPing();
            }
            else
            {
                List<Tuple<string, string>> to_ping = GetToPing();
                List<Tuple<string, string>> diff = _last_to_ping.Except(to_ping).ToList();

                if(diff.Count != 0)
                {
                    _last_to_ping = to_ping;
                    if(_pinger != null)
                    {
                        _pinger.Stop();
                        _pinger = null;
                    }
                }
            }

            if(_pinger == null)
            {
                _pinger = new ThreadedPinger(_last_to_ping, _num_pingers);
                _pinger.Start();
            }

            ListData<PingResult> d = new ListData<PingResult>(Context);

            lock(_pinger.P.Pings)
                d.Data.AddRange(_pinger.P.Pings);

            return new CollectedData(Context, true, d);
        }

        private List<Tuple<string, string>> GetToPing()
        {
            // Find the IP addresses to ping. This will typically provide the IP addresses
            // of the devices being monitored, and can also provide a subnet to ping, and
            // also any extra addresses to ping.
            IPAddressRequest request = new IPAddressRequest("PingCollector");
            RequestBus.Instance.MakeRequest(request);
            if (request.IsHandled == false)
                return null;

            _num_pingers = request.NumPingers;

            Dictionary<string, string> ip_to_name_map = new Dictionary<string, string>();
            request.IPAddresses.ForEach(i => ip_to_name_map[i.Item1] = i.Item2);
            List<Tuple<string, string>> to_ping = new List<Tuple<string, string>>(request.IPAddresses);

            // See if a full subnet ping was requested
            foreach (string s in request.Subnets)
            {
                if (IPAddress.TryParse(s, out IPAddress subnet))
                {
                    byte[] ping_addr = subnet.GetAddressBytes();

                    // Collect all the pingable IP addresses on the specified subnet.
                    // 0 and 255 are reserved, so no need to ping them.
                    for (byte i = 1; i < 255; ++i)
                    {
                        ping_addr[3] = i;
                        IPAddress addr = new IPAddress(ping_addr);

                        // Get the name of the device, if we happen to know it
                        string name = string.Empty;
                        ip_to_name_map.TryGetValue(addr.ToString(), out name);

                        to_ping.Add(Tuple.Create(addr.ToString(), name));
                    }
                }
            }

            // Remove any duplicate IP addresses that might have gotten in there
            to_ping.Sort((a, b) => string.Compare(a.Item1, b.Item1));
            to_ping = to_ping.Distinct().ToList();

            return to_ping;
        }

        /// <summary>
        /// Create a Data object with Ping results from the specified string. The string would
        /// be a JSON-ified result from earlier.
        /// </summary>
        /// <param name="context">The context the Data will belong to</param>
        /// <param name="value">The JSON string with results.</param>
        /// <returns></returns>
        public static Data Create(DataCollectorContext context, string value)
        {
            ListData<PingResult> d = new ListData<PingResult>(context);
            var definition = new { Value = new List<PingResult>() };
            var data = JsonConvert.DeserializeAnonymousType(value, definition);
            if (data != null)
                d.Data.AddRange(data.Value);
            return d;
        }

        private ThreadedPinger _pinger;
        private List<Tuple<string, string>> _last_to_ping;
        private uint _num_pingers;
    }

    /// <summary>
    /// Adapted from http://stackoverflow.com/a/19244196/706747
    /// </summary>
    public class MACAddressRetriever
    {
        public Dictionary<string, string> IPToMACMap { get; private set; }

        public MACAddressRetriever()
        {
            IPToMACMap = new Dictionary<string, string>();
        }

        public void SetMAC(PingResult result)
        {
            string ip = result.Address.ToString();
            if (IPToMACMap.TryGetValue(ip, out string mac))
                result.MAC = mac;
        }

        public void Retrieve()
        {
            IPToMACMap.Clear();

            try
            {
                System.Diagnostics.Process pProcess = new System.Diagnostics.Process();
                pProcess.StartInfo.FileName = "arp";
                pProcess.StartInfo.Arguments = "-a ";
                pProcess.StartInfo.UseShellExecute = false;
                pProcess.StartInfo.RedirectStandardOutput = true;
                pProcess.StartInfo.CreateNoWindow = true;
                pProcess.Start();
                string cmdOutput = pProcess.StandardOutput.ReadToEnd();

                foreach (Match m in _regex.Matches(cmdOutput))
                {
                    string ip = m.Groups["ip"].Value;
                    string mac = m.Groups["mac"].Value.ToUpper();

                    IPToMACMap[ip] = mac;
                }
            }
            catch (Exception)
            {
            }
        }

        private Regex _regex = new Regex(@"(?<ip>([0-9]{1,3}\.?){4})\s*(?<mac>([a-f0-9]{2}-?){6})", RegexOptions.IgnoreCase);
    }
}
