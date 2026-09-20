using System;
using System.Threading;
using System.Windows.Forms;

namespace NxMcpPlugin
{
    /// <summary>
    /// NX main thread dispatcher — Hidden Form + Invoke approach
    ///
    /// In NX managed plugin environment, SynchronizationContext.Current is null,
    /// and WinForms Timer does not trigger Tick in NX environment (NX does not pump WM_TIMER).
    ///
    /// This approach uses the Hidden Form's Invoke mechanism:
    /// 1. Create hidden form and CreateHandle() in Startup (main thread)
    /// 2. Call form.Invoke(action) from TCP background thread
    /// 3. Invoke posts to Form's window procedure via SendMessage(WM_USER+...)
    /// 4. NX message loop processes message, callback executes on main thread
    ///
    /// Limitation: if NX main thread is blocked (e.g. modal dialog), Invoke will timeout.
    /// </summary>
    public static class MainThreadDispatcher
    {
        private static Form _hiddenForm;
        private static bool _initialized = false;
        private static int _mainThreadId;
        private static readonly object _lock = new object();
        private static int _initCallCount = 0;

        /// <summary>
        /// Whether initialized
        /// </summary>
        public static bool IsInitialized
        {
            get { lock (_lock) return _initialized; }
        }

        /// <summary>
        /// Initialize dispatcher on NX main thread.
        /// Must be called in NX main thread (Startup callback).
        ///
        /// Create hidden form, its handle bound to main thread.
        /// Subsequent form.Invoke() will post to main thread via SendMessage.
        /// </summary>
        public static void Initialize()
        {
            _initCallCount++;
            Log(string.Format(">>> Initialize() called #{0}, _initialized={1}, thread={2}",
                _initCallCount, _initialized, Thread.CurrentThread.ManagedThreadId));

            lock (_lock)
            {
                if (_initialized)
                {
                    Log("Initialize: Already initialized, skipping");
                    return;
                }

                try
                {
                    _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                    Log(string.Format("Initialize: Current thread ID={0}", _mainThreadId));

                    // Create hidden form — not displayed, only for getting main thread window handle
                    _hiddenForm = new Form();
                    _hiddenForm.ShowInTaskbar = false;
                    _hiddenForm.WindowState = FormWindowState.Minimized;
                    _hiddenForm.Visible = false;
                    // CreateHandle() ensures window handle is created on current thread (main thread)
                    var handle = _hiddenForm.Handle;

                    _initialized = true;
                    Log(string.Format(
                        "Initialize OK: Main thread ID={0}, HiddenForm handle=0x{1:X}",
                        _mainThreadId, handle.ToInt64()));
                }
                catch (Exception ex)
                {
                    // No longer silently fallback — direct execution would trigger NX "can only call from main thread" warning
                    Log("Initialize FAIL: " + ex.GetType().Name + " — " + ex.Message);
                    Log("Initialize FAIL: Stack trace: " + ex.StackTrace);
                    _initialized = false;
                }
            }
        }

        /// <summary>
        /// Synchronously execute operation on main thread (block calling thread until completion)
        ///
        /// Use form.Invoke() to marshal to main thread via SendMessage.
        /// NXOpen API requires main thread execution, otherwise throws "can only call function from main thread" exception.
        /// </summary>
        /// <param name="action">Operation to execute on main thread</param>
        /// <param name="timeoutMs">Timeout in milliseconds, default 60 seconds</param>
        /// <returns>true indicates successful execution, false indicates timeout or error</returns>
        public static bool Invoke(Action action, int timeoutMs = 60000)
        {
            if (action == null) return true;

            // When not initialized: cannot directly execute NXOpen API on background thread
            // (would throw "can only call function from main thread")
            // Return false to let caller know scheduling failed
            if (!_initialized)
            {
                Log("Invoke FAIL: Not initialized, cannot safely execute NXOpen API. Ensure Initialize() is called in Startup()");
                return false;
            }

            // If currently on main thread, execute directly (avoid deadlock)
            if (IsMainThread())
            {
                try { action(); return true; }
                catch (Exception ex)
                {
                    Log("Invoke (main thread direct execution) exception: " + ex.Message);
                    return false;
                }
            }

            // Background thread: synchronously marshal to main thread via form.Invoke
            if (_hiddenForm == null || _hiddenForm.IsDisposed)
            {
                Log("Invoke FAIL: HiddenForm unavailable (null=" + (_hiddenForm == null) +
                    ", disposed=" + (_hiddenForm != null && _hiddenForm.IsDisposed) +
                    "), cannot marshal to main thread");
                return false;
            }

            try
            {
                // Synchronous Invoke — block TCP thread until main thread completes execution
                // NX main thread message loop processes SendMessage, callback executes on main thread
                Log("Invoke: Starting synchronous dispatch to main thread (handle=0x" + _hiddenForm.Handle.ToInt64().ToString("X") + ")");
                _hiddenForm.Invoke(action);
                Log("Invoke: Main thread execution completed");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // Invoke requires calling on thread that owns the window — HiddenForm handle is no longer valid
                // No longer fallback to direct execution (would trigger NX "can only call from main thread" warning)
                Log("Invoke FAIL: InvalidOperationException — " + ex.Message +
                    ". HiddenForm handle may be invalid, needs re-initialization");
                return false;
            }
            catch (Exception ex)
            {
                Log("Invoke FAIL: " + ex.GetType().Name + " — " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Asynchronously execute operation on main thread (without waiting for completion)
        /// </summary>
        public static void BeginInvoke(Action action)
        {
            if (action == null) return;

            if (!_initialized || IsMainThread())
            {
                try { action(); } catch { }
                return;
            }

            if (_hiddenForm == null || _hiddenForm.IsDisposed)
            {
                try { action(); } catch { }
                return;
            }

            try
            {
                _hiddenForm.BeginInvoke(action);
            }
            catch { }
        }

        /// <summary>
        /// Stop dispatcher, clean up resources
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                if (_hiddenForm != null)
                {
                    try { _hiddenForm.Close(); _hiddenForm.Dispose(); } catch { }
                    _hiddenForm = null;
                }
                _initialized = false;
                Log("Closed");
            }
        }

        /// <summary>
        /// Check if currently on main thread
        /// </summary>
        private static bool IsMainThread()
        {
            return Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        private static void Log(string msg)
        {
            string line = string.Format("[{0:HH:mm:ss.fff}] [Dispatcher] {1}", DateTime.Now, msg);
            try { Console.WriteLine(line); } catch { }
            try
            {
                var logPath = NxPaths.StartupFile("tcp_server.log");
                System.IO.File.AppendAllText(logPath, line + "\n");
            }
            catch { }
        }
    }
}
