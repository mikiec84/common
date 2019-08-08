using COMMONWeb;
using gov.sandia.sld.common.configuration;
using gov.sandia.sld.common.dailyfiles;
using gov.sandia.sld.common.data;
using gov.sandia.sld.common.db;
using gov.sandia.sld.common.db.interpreters;
using gov.sandia.sld.common.db.models;
using gov.sandia.sld.common.db.responders;
using gov.sandia.sld.common.logging;
using gov.sandia.sld.common.requestresponse;
using gov.sandia.sld.common.utilities;
using Nancy.Hosting.Self;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;

namespace gov.sandia.sld.common
{
    partial class COMMONService : ServiceBase
    {
        public COMMONService()
        {
            InitializeComponent();

            logging.EventLog.GlobalSource = "COMMONService";

            try
            {
                LogManager.InitializeConfigurator("common_log_config.json");
            }
            catch (Exception)
            {
            }

            m_daily_file_writer = new Writer();
            m_shutdown = new ManualResetEvent(false);

            m_last_configuration_update = DateTimeOffset.MinValue;
            m_system_device = null;
            m_days_to_keep = 180;
            m_daily_file_cleaner = new dailyfiles.Cleaner(m_days_to_keep);
            m_db_cleaner = new db.Cleaner();

            m_responders = new List<Responder>(new Responder[] {
                new EventLogResponder(),
                new DatabaseInfoResponder(),
                new AttributeResponder(),
                new IPAddressResponder(),
                new COMMONDBSizeResponder(),
                new MonitoredDrivesResponder(),
                //new SMARTFailureResponder(),
            });

            m_interpreters = BaseInterpreter.AllInterpreters();

            m_host_configuration = new HostConfiguration()
            {
                UrlReservations = new UrlReservations() { CreateAutomatically = true }
            };
            m_host = new NancyHost(new Uri("http://localhost:8080"), new COMMONDatabaseBootstrapper(), m_host_configuration);
        }

        protected override void OnStart(string[] args)
        {
            Startup();
        }

        protected override void OnStop()
        {
            Shutdown();
        }

        public void Startup()
        {
            if (m_thread != null)
                throw new Exception("Startup: Starting while already running");

            logging.EventLog elog = new ApplicationEventLog();

            elog.LogInformation("Starting");
            GlobalIsRunning.IsRunning = true;
            m_shutdown.Reset();

            elog.LogInformation("Initializing database");
            Stopwatch watch = Stopwatch.StartNew();
            Database db = new Database();
            new Initializer(null).Initialize(db);
            elog.LogInformation($"Database initialization took {watch.ElapsedMilliseconds} ms");

            using (SQLiteConnection conn = db.Connection)
            {
                conn.Open();

                db.Attribute attr = new db.Attribute();
                attr.Set("service.startup_time", DateTimeOffset.Now.ToString("o"), conn);
                string assembly_ver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                attr.Set("software.version", assembly_ver, conn);
            }

            elog.LogInformation("Setting up responders");
            m_responders.ForEach(r => RequestBus.Instance.Subscribe(r));

            elog.LogInformation("Starting web server");
            watch.Restart();
            m_host.Start();
            elog.LogInformation($"Web server startup took {watch.ElapsedMilliseconds} ms");

            elog.LogInformation("Starting work thread");
            watch.Restart();
            m_thread = new Thread(new ThreadStart(ThreadFunc));
            m_thread.Start();
            // Wait for the thread to start
            while (!m_thread.IsAlive)
                Thread.Sleep(25);
            elog.LogInformation($"Work thread startup took {watch.ElapsedMilliseconds} ms");

            elog.LogInformation("Completed startup");
        }

        public void Shutdown()
        {
            logging.EventLog elog = new ApplicationEventLog();

            elog.LogInformation("Stopping");
            Stopwatch watch = Stopwatch.StartNew();
            GlobalIsRunning.IsRunning = false;

            m_shutdown.Set();
            while (m_thread != null && m_thread.ThreadState == System.Threading.ThreadState.Running)
                Thread.Sleep(100);

            elog.LogInformation($"Stopping worker thread took {watch.ElapsedMilliseconds} ms");

            Database db = new Database();
            using (SQLiteConnection conn = db.Connection)
            {
                conn.Open();
                db.Attribute attr = new db.Attribute();
                attr.Set("service.stop_time", DateTimeOffset.Now.ToString("o"), conn);
            }

            elog.LogInformation("Stopping web server");
            m_host.Stop();
            m_host.Dispose();

            elog.LogInformation("Clearing responders");
            m_responders.ForEach(r => RequestBus.Instance.Unsubscribe(r));

            m_thread = null;
            elog.LogInformation("Completed stopping");
        }

        protected void ThreadFunc()
        {
            try
            {
                int max_collections_per_pass = 10;
                logging.EventLog elog = new ApplicationEventLog();
                Database db = new Database();
                DataStorage storage = new DataStorage();
                m_interpreters.ForEach(i => storage.AddInterpreter(i));

                while (GlobalIsRunning.IsRunning)
                {
                    int collector_count = 0;

                    using (SQLiteConnection conn = db.Connection)
                    {
                        conn.Open();

                        CheckForConfigurationChanges(storage, conn);

                        // Used to hold which collector was doing its thing if/when an exception occurs.
                        // It's used in the exception handler.
                        string collector_name = string.Empty;

                        try
                        {
                            DBCollectionTimeRetriever retriever = new DBCollectionTimeRetriever(conn);

                            // Gets the list of things that need to be collected right now. They'll
                            // be in the order they should be collected.
                            List<DataCollector> collectors = m_system_device.GetCollectors(retriever);
                            collector_count = collectors.Count;

                            // Limit this to the top 10 or so
                            while (collectors.Count > max_collections_per_pass)
                                collectors.RemoveAt(collectors.Count - 1);

                            foreach (DataCollector collector in collectors)
                            {
                                collector_name = collector.Context.Name;
                                //elog.LogInformation($"Collecting {collector_name}");

                                Stopwatch watch = Stopwatch.StartNew();

                                // Records that the collector is being collected, updates the next collection
                                // time, and records when the collection attempt was started. When it's destroyed
                                // when exiting the using, it records that it is no longer being collected.
                                //
                                // This was done so even if an exception occurs within Acquire(), the flag
                                // that the collector is being collected will be cleared.
                                using (BeingCollected bc = new BeingCollected(collector.Context.ID.ID, conn))
                                {
                                    collector.Acquire();
                                }

                                //long elapsed_ms = watch.ElapsedMilliseconds;
                                //if(elapsed_ms > 500)
                                //    elog.LogInformation($"Collecting {collector_name} took {elapsed_ms} ms");

                                if (GlobalIsRunning.IsRunning == false)
                                    break;  // out of the foreach loop
                            }
                        }
                        catch (Exception e)
                        {
                            elog.LogError($"Exception from within {collector_name}");
                            elog.Log(e);
                        }

                        // Will write the daily file when it's the right time to do so; otherwise, it does nothing.
                        if(GlobalIsRunning.IsRunning)
                            m_daily_file_writer.DoWrite(conn);

                        if (GlobalIsRunning.IsRunning)
                            m_db_cleaner.CleanOldData(m_days_to_keep, conn);
                    }

                    // Deletes any old daily files
                    if (GlobalIsRunning.IsRunning)
                        m_daily_file_cleaner.DoClean();

                    // Delete any old log files too, if it's time to do so.
                    if (GlobalIsRunning.IsRunning)
                        LogManager.CleanOldData();

                    // And make sure we update our logging if it changed
                    if (GlobalIsRunning.IsRunning)
                        LogManager.CheckConfiguration();

                    // m_shutdown will be reset when the thread starts, and set when it's time to
                    // stop the thread. So this will wait if this event hasn't been
                    // set, but will return immediately if it has been set.
                    //
                    // If there's still more data to collect let's make another run right away
                    if(GlobalIsRunning.IsRunning && collector_count < max_collections_per_pass)
                        m_shutdown.WaitOne(TimeSpan.FromSeconds(10));
                }

                m_interpreters.ForEach(i => storage.RemoveInterpreter(i));
            }
            catch (Exception e)
            {
                logging.EventLog elog = new ApplicationEventLog();
                elog.Log(e);

                m_shutdown.Reset();
                m_thread.Abort();
                m_thread = null;
            }
        }

        private void CheckForConfigurationChanges(DataStorage storage, SQLiteConnection conn)
        {
            Database db = new Database();

            DateTimeOffset? configuration_update = db.GetLastConfigurationUpdateAttribute(conn);

            if (m_last_configuration_update == DateTimeOffset.MinValue || (configuration_update.HasValue && configuration_update.Value != m_last_configuration_update))
            {
                if (configuration_update.HasValue)
                    m_last_configuration_update = configuration_update.Value;

                logging.EventLog elog = new ApplicationEventLog();
                elog.LogInformation("Loading configuration from database");
                //db.Initialize();

                SystemConfiguration config = SystemConfigurationStore.Get(false, conn);

                bool is_pinging = false;
                if (config.System != null)
                {
                    CollectorInfo ping = config.System.collectors.Find(c => c.collectorType == ECollectorType.Ping);
                    is_pinging = (ping != null) && ping.isEnabled;
                }

                // Do this outside of the if(is_pinging) block so if pinging was disabled we'll be fine
                if (m_threaded_pinger != null)
                {
                    m_threaded_pinger.Stop();
                    m_threaded_pinger = null;
                }
                m_pinger = null;

                if (is_pinging)
                {
                    Tuple<List<Tuple<string, string>>, uint> to_ping = GetToPing();
                    m_pinger = new Pinger(to_ping.Item1);
                    m_threaded_pinger = new ThreadedPinger(m_pinger, to_ping.Item2, TimeSpan.FromSeconds(30));
                }

                m_system_device = new SystemDevice(config, storage, m_pinger);

                DeleteDays delete_days = new DeleteDays();
                int? days = delete_days.GetValueAsInt(conn);
                m_days_to_keep = days ?? 180;
                m_daily_file_cleaner.DaysToKeep = m_days_to_keep;
            }
        }

        /// <summary>
        /// Gets the set of things to ping, as well as how many pingers to use
        /// </summary>
        /// <returns>A list of the devices to ping, and the # of pingers to use</returns>
        private Tuple<List<Tuple<string, string>>, uint> GetToPing()
        {
            // Find the IP addresses to ping. This will typically provide the IP addresses
            // of the devices being monitored, and can also provide a subnet to ping, and
            // also any extra addresses to ping.
            IPAddressRequest request = new IPAddressRequest("COMMONService");
            RequestBus.Instance.MakeRequest(request);
            if (request.IsHandled == false)
                return null;

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

            return Tuple.Create(to_ping, request.NumPingers);
        }

        private Writer m_daily_file_writer;
        private dailyfiles.Cleaner m_daily_file_cleaner;
        private db.Cleaner m_db_cleaner;
        private Thread m_thread;
        private ManualResetEvent m_shutdown;

        private Pinger m_pinger;
        private ThreadedPinger m_threaded_pinger;

        private SystemDevice m_system_device;
        private DateTimeOffset m_last_configuration_update;
        private int m_days_to_keep;

        private HostConfiguration m_host_configuration;
        private NancyHost m_host;

        private List<Responder> m_responders;
        private List<IDataInterpreter> m_interpreters;
    }
}
