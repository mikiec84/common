using System;
using System.Threading;

// http://stackoverflow.com/a/4042887/706747

namespace gov.sandia.sld.common.utilities
{
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
                    bool is_complete = false;
                    ManualResetEvent[] events = { _stop_event };

                    while (is_complete == false)
                    {
                        DateTime start = DateTime.Now;

                        _callback();

                        // Lets make sure we take into account how long the _callback()
                        // took so we wait for the appropriate amount of time
                        TimeSpan duration = DateTime.Now - start;
                        TimeSpan wait = duration > _frequency ? TimeSpan.FromMilliseconds(0) : _frequency - duration;

                        is_complete = ManualResetEvent.WaitAny(events, wait) == 0;
                    }
                });
            _stop_event.Reset();
            _thread.Start();
        }

        public void Stop()
        {
            if (_thread == null)
                return;
            _stop_event.Set();
            _thread.Join();
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
    }
}
