using gov.sandia.sld.common.configuration;
using gov.sandia.sld.common.logging;
using gov.sandia.sld.common.utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// http://stackoverflow.com/a/4042887/706747

namespace gov.sandia.sld.common.data
{
    public class PingResult
    {
        /// <summary>
        /// The IP address that is being pinged
        /// </summary>
        [JsonConverter(typeof(IPAddressConverter))]
        public IPAddress Address { get; set; }
        /// <summary>
        /// True if we think the address is pingable
        /// </summary>
        public bool IsPingable { get; set; }
        /// <summary>
        /// The average response time in ms
        /// </summary>
        public long AvgTime { get; set; }
        /// <summary>
        /// The name of the device at the IP address, if known
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// The MAC address at the IP address, if it can be determined
        /// </summary>
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

        /// <summary>
        /// Is considered different if the pingable status changed
        /// or if the MAC address changed
        /// </summary>
        /// <param name="other">The other PingResult to compare this one with</param>
        /// <returns>True if the ping state has changed</returns>
        public bool IsDifferent(PingResult other)
        {
            return IsPingable != other.IsPingable ||
                MAC != other.MAC;
        }

        /// <summary>
        /// Ping the IP address in Address. Will do the pinging the specified
        /// number of times, and will update the IsPingable property if at least
        /// half of the pings were successful. It will also update the AvgTime property
        /// with the average of the response times.
        /// 
        /// The timeout is 1 second, so if the pings all fail, it will take 1*repeat
        /// seconds to return, and the AvgTime will be 1000
        /// </summary>
        /// <param name="repeat">The number of times to ping in succession</param>
        /// <returns>True if we were able to attempt the pings and the IsPingable and AvgTime
        /// properties were updated</returns>
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
                        PingReply reply = pinger.Send(Address, timeout);
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
                AvgTime = (long)(((double)ms / (double)repeat) + 0.5f);

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
    /// Used to ping a series of IP addresses. Does so by creating a number
    /// of threads, and each thread pings a chunk of addresses. The number
    /// of threads created is specified by the num_pingers param to DoPings().
    /// Each thread will ping roughly the same number of addresses.
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

        /// <summary>
        /// Pings each of the addresses specified in the Pings list. The pings are done
        /// in spearate threads, but this method will block until they're all complete.
        /// </summary>
        /// <param name="num_pingers"></param>
        public void DoPings(uint num_pingers)
        {
            // Create some # of tasks for concurrent pinging. The number of tasks
            // will be around num_pingers.
            List<Task> tasks = new List<Task>();
            List<List<PingResult>> chunks = Pings.ChunkBy((int)((uint)Pings.Count / num_pingers));

            foreach (List<PingResult> addrs in chunks)
            {
                // Create the number of threads specified in num_pingers and ping a chunk
                // of addresses in each thread
                Task t = new Task(
                    () =>
                    {
                        Stopwatch watch = Stopwatch.StartNew();

                        List<PingResult> local_addrs = addrs;
                        local_addrs.ForEach(a => a.DoPing());

                        Trace.WriteLine($"Pinging {local_addrs.Count} addrs took {watch.ElapsedMilliseconds} ms");
                    });
                tasks.Add(t);
            }

            if (tasks.Count > 0)
            {
                lock (Pings)
                {
                    try
                    {
                        Stopwatch watch = Stopwatch.StartNew();
                        tasks.ForEach(t => t.Start());
                        long starting = watch.ElapsedMilliseconds;

                        watch.Restart();
                        Task.WaitAll(tasks.ToArray());
                        long waited = watch.ElapsedMilliseconds;

                        Trace.WriteLine($"Starting took {starting} ms, waiting took {waited} ms");
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

    /// <summary>
    /// Tells a Pinger to do his pinging at the specified interval.
    /// </summary>
    public class ThreadedPinger : ThreadedWorker
    {
        public ThreadedPinger(Pinger pinger, uint num_pingers, TimeSpan interval)
            : base(interval)
        {
            _pinger = pinger;
            _callback = () => _pinger.DoPings(num_pingers);
        }

        private Pinger _pinger;
    }

    /// <summary>
    /// The DataCollector that collects ping data for the local network.
    /// </summary>
    public class PingCollector : DataCollector
    {
        public Pinger Pinger { get; set; }

        public PingCollector(CollectorID id)
            : base(new DataCollectorContext(id, ECollectorType.Ping))
        {
        }

        public override CollectedData OnAcquire()
        {
            ListData<PingResult> d = new ListData<PingResult>(Context);

            // When it's time to collect the data, just grab the latest state
            // from the Pinger we've been told to use.
            if (Pinger != null)
            {
                lock (Pinger.Pings)
                    d.Data.AddRange(Pinger.Pings);
            }

            return new CollectedData(Context, true, d);
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
