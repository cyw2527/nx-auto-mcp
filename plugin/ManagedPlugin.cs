using System;
using System.Linq;
using System.Reflection.Emit;
using System.Threading;
using NxMcpPlugin.Tools;

namespace NxMcpPlugin
{
    /// <summary>
    /// NX managed plugin entry point
    ///
    /// NX discovers and loads this plugin via ManagedLoader.dll.
    /// Entry functions: Startup (on load), Shutdown (on unload), GetUnloadOption
    ///
    /// Menu callback registration (verified via NXOpenUI.xml):
    ///   UI.GetUI().MenuBarManager.AddMenuAction(name, callback)
    ///   - name: must exactly match the BUTTON name in the .men file
    ///   - callback: ActionCallback delegate, signature void(MenuButtonEvent)
    ///
    /// Deployment:
    ///   1. Compile managed_plugin.dll
    ///   2. Copy to {NX_ROOT}\NXBIN\managed\managed_plugin.dll
    ///   3. Copy nx_mcp_plugin.men to {UGII_SITE_DIR}\startup\
    ///   4. NX auto-loads on startup
    /// </summary>
    public class ManagedPlugin
    {
        private static TcpServer _server;
        private static SelectionMonitor _monitor;
        private static ContextMenuHandler _contextHandler;
        private static Thread _serverThread;
        private static bool _assemblyResolveRegistered = false;
        private static bool _startupAttempted = false;
        private static int _startupCount = 0;
        private static readonly object _initLock = new object();
        // Prevent GC from collecting delegates registered via AddMenuAction
        private static System.Collections.Generic.List<Delegate> _menuDelegates = new System.Collections.Generic.List<Delegate>();

        /// <summary>
        /// Automatically called on NX startup (discovered by ManagedLoader)
        /// </summary>
        public static int Startup(string[] args)
        {
            // === Ultra-early log: write to a standalone file to capture events even if Log() fails ===
            var earlyLogPath = NxPaths.StartupFile("nx_plugin_early.log");
            try
            {
                string earlyLine = string.Format("[{0:HH:mm:ss.fff}] [EARLY] Startup called, args={1}\n",
                    DateTime.Now, args != null ? args.Length : -1);
                System.IO.File.AppendAllText(earlyLogPath, earlyLine);
            }
            catch { }

            return DoStartup(args);
        }

        /// <summary>
        /// Called when NX menu ACTIONS are triggered.
        /// Since v0.3.1, Main() directly dispatches button events instead of relying on AddMenuAction (which may fail across load contexts).
        /// args[0] contains the BUTTON name, e.g. "NX_MCP_START_SERVER".
        /// Outermost try/catch prevents unhandled exceptions from causing e0434352 crashes.
        /// </summary>
        public static void Main(string[] args)
        {
            try
            {
                // Extract button name
                string buttonName = (args != null && args.Length > 0) ? args[0] : "";
                Log(string.Format("[Main] >>> Main() called, args={0}, buttonName='{1}'",
                    args != null ? args.Length : -1, buttonName));

                // Use lock to protect initialization check
                lock (_initLock)
                {
                    // If Startup hasn't run yet, initialize first
                    if (_server == null)
                    {
                        int port = 1977;
                        string envPort = Environment.GetEnvironmentVariable("NX_MCP_PORT");
                        int p;
                        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out p))
                            port = p;

                        if (IsPortInUse(port))
                        {
                            // Port in use → old instance still running, just re-register callbacks
                            try
                            {
                                dynamic ui = GetUI();
                                RegisterMenuCallbacks(ui);
                            }
                            catch { }
                        }
                        else
                        {
                            DoStartup(args);
                        }
                    }
                }

                // Directly dispatch button event
                if (!string.IsNullOrEmpty(buttonName))
                    Log(string.Format("[Main] Button event: {0}", buttonName));
                DispatchAction(buttonName);
            }
            catch (Exception ex)
            {
                try { Log(string.Format("[Main] Exception (caught): {0}", ex.Message)); }
                catch { }
            }
        }

        /// <summary>
        /// Dispatch directly to corresponding handler by button name
        /// AddMenuAction also registers the same callbacks, but Main() direct dispatch is more reliable
        /// </summary>
        private static void DispatchAction(string buttonName)
        {
            switch (buttonName)
            {
                case "NX_MCP_ASK_AI":
                    if (_contextHandler != null) _contextHandler.OnAskAi();
                    break;
                case "NX_MCP_TOGGLE_SERVER":
                    ToggleServer();
                    break;
                case "NX_MCP_SHOW_STATUS":
                    ShowStatus();
                    break;
                case "NX_MCP_HOVER_CAPTURE":
                    if (_contextHandler != null) _contextHandler.OnHoverCapture();
                    break;
                default:
                    break;
            }
        }

        private static void ToggleServer()
        {
            try
            {
                if (_server == null)
                {
                    ShowMessage("MCP Server", "Server not initialized. Restart NX to auto-start.");
                    return;
                }
                if (_server.IsRunning)
                {
                    StopServer();
                    return;
                }
                // Restart existing server — do NOT create new TcpServer
                // (NX menu callback CAS prevents NXOpen API calls from here)
                int port = 1977;
                string envPort = Environment.GetEnvironmentVariable("NX_MCP_PORT");
                int p;
                if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out p)) port = p;
                _serverThread = new Thread(() => _server.Start("127.0.0.1", port));
                _serverThread.IsBackground = true;
                _serverThread.Start();
                Log(string.Format("[CTRL] Server started on port {0}", port));
                ShowMessage("MCP Server", string.Format("Server started on 127.0.0.1:{0}", port));
            }
            catch (Exception ex) { ShowMessage("MCP Server Error", ex.Message); }
        }

        private static void StopServer()
        {
            try
            {
                if (_server != null && _server.IsRunning)
                {
                    _server.Stop();
                    Log("[CTRL] Server stopped");
                    ShowMessage("MCP Server", "Server stopped.");
                }
                else
                {
                    ShowMessage("MCP Server", "Server is not running.");
                }
            }
            catch (Exception ex) { ShowMessage("MCP Server Error", ex.Message); }
        }

        private static void ShowStatus()
        {
            try
            {
                bool running = _server != null && _server.IsRunning;
                int port = 1977;
                string envPort = Environment.GetEnvironmentVariable("NX_MCP_PORT");
                int ep = 0; if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out ep)) port = ep;
                // Version and tool count use real values — not hardcoded (hardcoded "v0.3.2 / Tools: 102" wouldn't match reality)
                var registry = ToolRegistry.Instance;
                int toolCount = registry != null ? registry.ToolCount : 0;
                string status = string.Format(
                    "NX MCP Plugin v{0}\nServer: {1}\nPort: {2}\nTools: {3}\nMonitor: {4}",
                    PluginVersion.Full,
                    running ? "RUNNING" : "STOPPED", port, toolCount,
                    _monitor != null ? "Active" : "Inactive");
                ShowMessage("MCP Plugin Status", status);
                Log("[STATUS] " + status.Replace("\n", " | "));
            }
            catch (Exception ex) { ShowMessage("Status Error", ex.Message); }
        }

        private static void ShowMessage(string title, string msg)
        {
            try
            {
                dynamic ui = GetUI();
                if (ui != null)
                {
                    var uiType = ((object)ui).GetType();
                    var nxMsgType = uiType.Assembly.GetType("NXOpen.NXMessageBox");
                    if (nxMsgType != null)
                    {
                        var showMethod = nxMsgType.GetMethod("Show",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                            null, new[] { typeof(string), typeof(NXOpen.NXMessageBox.DialogType), typeof(string) }, null);
                        if (showMethod != null)
                        {
                            var dialogType = Enum.Parse(nxMsgType.GetNestedType("DialogType"), "Information");
                            showMethod.Invoke(null, new object[] { title, dialogType, msg });
                        }
                        return;
                    }
                }
            }
            catch { }
            Log(string.Format("[MSG] {0}: {1}", title, msg));
        }

        private static int DoStartup(string[] args)
        {
            lock (_initLock)
            {
                Interlocked.Increment(ref _startupCount);
                Log(string.Format("[INIT] DoStartup called #{0}", _startupCount));

                // Prevent duplicate initialization (both Startup and Main may call this)
                // If _server already exists, skip initialization
                if (_server != null)
                {
                    Log(string.Format("[INIT] Skipping duplicate initialization (#{0}), _server already exists", _startupCount));
                    return 0;
                }

                // Check if port is already in use (after NX resets static fields, old TcpServer may still be running)
                int port = 1977;
                string envPort = Environment.GetEnvironmentVariable("NX_MCP_PORT");
                int p;
                if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out p))
                    port = p;

                if (IsPortInUse(port))
                {
                    Log(string.Format("[INIT] Skipping duplicate initialization (#{0}), port {1} already in use (old instance still running)", _startupCount, port));
                    // Port in use means old TcpServer is still running, just re-register callbacks
                    try
                    {
                        dynamic ui = GetUI();
                        RegisterMenuCallbacks(ui);
                        Log("[INIT] Menu callbacks re-registered");
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format("[INIT] Failed to re-register callbacks: {0}", ex.Message));
                    }
                    return 0;
                }

            var earlyLogPath = NxPaths.StartupFile("nx_plugin_early.log");

            try
            {
                // Key: register AssemblyResolve hook so .NET can load assemblies from NXBIN/managed/
                // NX does not add NXBIN/managed/ to the Probing Path, causing lazy-loaded assemblies to not be found
                // Use flag to avoid duplicate registration (prevent duplicate addition after NX resets static fields)
                if (!_assemblyResolveRegistered)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveNxAssembly;
                    _assemblyResolveRegistered = true;
                    Log("[INIT] AssemblyResolve registered");
                }

                dynamic session = NXOpen.Session.GetSession();
                Console.Error.WriteLine("[DIAG] Session obtained");
                dynamic ui = GetUI();
                Console.Error.WriteLine("[DIAG] UI obtained");

                // Initialize main thread dispatcher — allow TCP background thread tool calls to marshal to NX main thread
                Console.Error.WriteLine("[DIAG] About to call MainThreadDispatcher.Initialize()...");
                Log("[INIT] >>> About to call MainThreadDispatcher.Initialize()...");
                try
                {
                    MainThreadDispatcher.Initialize();
                    Console.Error.WriteLine("[DIAG] MainThreadDispatcher.Initialize() returned OK");
                    Log("[INIT] >>> MainThreadDispatcher.Initialize() returned successfully");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[DIAG] MainThreadDispatcher.Initialize() EXCEPTION: " + ex.Message);
                    Log(string.Format("[INIT] >>> MainThreadDispatcher.Initialize() exception: {0}", ex.Message));
                }
                Console.Error.WriteLine("[DIAG] IsInitialized=" + MainThreadDispatcher.IsInitialized);
                Log(string.Format("[INIT] MainThreadDispatcher.IsInitialized={0}", MainThreadDispatcher.IsInitialized));

                Log(string.Format("NX MCP Plugin v{0} starting (#{1})", PluginVersion.Full, _startupCount));

                // Create selection listener (check if already exists to avoid duplicate SelectionSubscriber)
                if (_monitor == null)
                {
                    _monitor = new SelectionMonitor(ui);
                    _monitor.Start();
                    Log("Selection listener started");
                }
                else
                {
                    Log("Selection listener already exists, skipping creation");
                }

                // Create context menu/shortcut handler
                _contextHandler = new ContextMenuHandler(session, ui, _monitor);
                Log("Context menu handler created");

                // Register menu callbacks (NXOpenUI.xml line 11577)
                // MenuBarManager.AddMenuAction(name, ActionCallback)
                // name must match the BUTTON name in the .men file
                RegisterMenuCallbacks(ui);

                // Create TCP service
                _server = new TcpServer(session, ui, _monitor);

                string host = "127.0.0.1";
                string envHost = Environment.GetEnvironmentVariable("NX_MCP_HOST");
                if (!string.IsNullOrEmpty(envHost))
                    host = envHost;

                _serverThread = new Thread(() => _server.Start(host, port));
                _serverThread.IsBackground = true;
                _serverThread.Start();

                Log(string.Format("Plugin ready: {0}:{1}", host, port));
                Log("Context menu: Select object → Right-click → Ask AI");
                Log("Shortcut: Ctrl+Shift+A");

                // Write a non-rotating status snapshot so diagnostics can find it
                // even after log rotation swallows the startup block.
                WriteStatusSnapshot(host, port);

                return 0;
            }
            catch (Exception ex)
            {
                // Also write early log
                try
                {
                    System.IO.File.AppendAllText(earlyLogPath,
                        string.Format("[{0:HH:mm:ss.fff}] [EARLY] Startup FAIL: {1}\n{2}\n",
                            DateTime.Now, ex.Message, ex.StackTrace));
                }
                catch { }
                Log(string.Format("Startup failed: {0}\n{1}", ex.Message, ex.StackTrace));
                return 1;
            }
            } // end lock (_initLock)
        }

        /// <summary>
        /// NX2412: NXOpen.UI class removed from SDK but EXISTS at runtime
        /// in NXOpenUI assembly (v2412). Type.GetType("NXOpen.UI", false) returns null,
        /// but scanning AppDomain assemblies finds it in NXOpenUI.
        /// </summary>
        private static dynamic GetUI()
        {
            try
            {
                var uiType = Type.GetType("NXOpen.UI", false);
                if (uiType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try { uiType = asm.GetType("NXOpen.UI"); if (uiType != null) break; }
                        catch { }
                    }
                }
                if (uiType != null)
                {
                    var getUI = uiType.GetMethod("GetUI",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (getUI != null)
                        return getUI.Invoke(null, null);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Check if port is already in use
        /// Used to detect whether the old TcpServer is still running after NX resets static fields
        /// </summary>
        private static bool IsPortInUse(int port)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return false; // Port available
            }
            catch (System.Net.Sockets.SocketException)
            {
                return true; // Port in use
            }
            catch
            {
                return true; // Other exception, assume port in use
            }
        }

        /// <summary>
        /// Register menu callbacks
        ///
        /// ActionCallback real signature (via runtime reflection):
        ///   CallbackStatus ActionCallback(MenuButtonEvent buttonEvent)
        ///   CallbackStatus: Continue=0, Cancel=1, OverrideStandard=2, Warning=3, Error=4
        ///
        /// Previously CS0407 occurred because callback returned void, but it should return CallbackStatus.
        /// After fix, direct static type calls work without reflection.
        /// </summary>
        private static void RegisterMenuCallbacks(dynamic ui)
        {
            Log("[MENU] Registering menu callbacks (v10 — DynamicMethod IL wrappers + reflection Invoke)");
            try
            {
                _menuDelegates.Clear(); // Clean up old delegates (if re-registering)
                dynamic mb = ui.MenuBarManager;
                Log(string.Format("[MENU] MenuBarManager: {0}", mb.GetType().FullName));

                var mbType = ((object)mb).GetType();
                var initCbType = mbType.Assembly.GetType("NXOpen.MenuBar.MenuBarManager+InitializeMenuApplication");
                var enterCbType = mbType.Assembly.GetType("NXOpen.MenuBar.MenuBarManager+EnterMenuApplication");
                var exitCbType = mbType.Assembly.GetType("NXOpen.MenuBar.MenuBarManager+ExitMenuApplication");
                var actionCbType = mbType.Assembly.GetType("NXOpen.MenuBar.MenuBarManager+ActionCallback");

                // 1. RegisterApplication — dynamic dispatch (works, verified)
                if (initCbType != null && enterCbType != null && exitCbType != null)
                {
                    var dInit = Delegate.CreateDelegate(initCbType, typeof(ManagedPlugin), "OnAppInit");
                    var dEnter = Delegate.CreateDelegate(enterCbType, typeof(ManagedPlugin), "OnAppEnter");
                    var dExit = Delegate.CreateDelegate(exitCbType, typeof(ManagedPlugin), "OnAppExit");
                    ((dynamic)mb).RegisterApplication("NX_MCP_PLUGIN",
                        (dynamic)dInit, (dynamic)dEnter, (dynamic)dExit, true, true, true);
                    Log("[MENU] RegisterApplication(NX_MCP_PLUGIN) registered");
                }

                // 2. AddMenuAction via reflection Invoke
                if (actionCbType != null)
                {
                    var addAction = mbType.GetMethod("AddMenuAction",
                        new[] { typeof(string), actionCbType });

                    if (addAction == null)
                    {
                        // Try listing all methods containing "AddMenu" or "Action" for diagnosis
                        var allMethods = mbType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        var candidates = allMethods.Where(m => m.Name.IndexOf("Action", StringComparison.OrdinalIgnoreCase) >= 0
                            || m.Name.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0).Take(5);
                        Log(string.Format("[MENU] AddMenuAction not found. Candidate methods: {0}",
                            string.Join(", ", candidates.Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"))));
                    }
                    else
                    {
                        Log(string.Format("[MENU] AddMenuAction found, trying constructor-based delegate"));

                        // Try using delegate constructor (object target, IntPtr method) to create ActionCallback
                        var cbCtor = actionCbType.GetConstructor(new[] { typeof(object), typeof(IntPtr) });
                        Log(string.Format("[MENU] ActionCallback ctor: {0}", cbCtor != null ? "found" : "NOT FOUND"));

                        foreach (var entry in new[] {
                            new { Action="NX_MCP_ASK_AI", Handler="OnAskAiActionObj" },
                            new { Action="NX_MCP_TOGGLE_SERVER", Handler="OnToggleServerActionObj" },
                            new { Action="NX_MCP_SHOW_STATUS", Handler="OnShowStatusActionObj" },
                            new { Action="NX_MCP_HOVER_CAPTURE", Handler="OnHoverCaptureActionObj" }
                        })
                        {
                            try
                            {
                                var handlerMethod = typeof(ManagedPlugin).GetMethod(entry.Handler,
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                                if (handlerMethod == null) { Log("[MENU] " + entry.Handler + " not found"); continue; }

                                Delegate cb = null;

                                // Option A: delegate constructor (object, IntPtr)
                                if (cbCtor != null)
                                {
                                    try
                                    {
                                        var methodPtr = handlerMethod.MethodHandle.GetFunctionPointer();
                                        cb = (Delegate)cbCtor.Invoke(new object[] { null, methodPtr });
                                        Log(string.Format("[MENU] {0} created successfully with constructor", entry.Action));
                                    }
                                    catch (Exception exCtor)
                                    {
                                        Log(string.Format("[MENU] {0} constructor failed: {1}", entry.Action, exCtor.Message));
                                    }
                                }

                                // Option B: DynamicMethod IL bridge
                                if (cb == null)
                                {
                                    var invokeMethod = actionCbType.GetMethod("Invoke");
                                    var cbReturnType = invokeMethod.ReturnType;
                                    var cbParamTypes = invokeMethod.GetParameters().Select(p => p.ParameterType).ToArray();

                                    var dm = new DynamicMethod("CB_" + entry.Handler, cbReturnType, cbParamTypes,
                                        typeof(ManagedPlugin).Module);
                                    var il = dm.GetILGenerator();
                                    il.Emit(OpCodes.Ldarg_0);
                                    il.Emit(OpCodes.Call, handlerMethod);
                                    il.Emit(OpCodes.Ret);
                                    cb = dm.CreateDelegate(actionCbType);
                                    Log(string.Format("[MENU] {0} created with DynamicMethod", entry.Action));
                                }

                                _menuDelegates.Add(cb);

                                // Register
                                addAction.Invoke(mb, new object[] { entry.Action, cb });
                                Log(string.Format("[MENU] {0} AddMenuAction invoked successfully", entry.Action));
                            }
                            catch (Exception ex)
                            {
                                Log(string.Format("[MENU] {0} failed: {1}", entry.Action, ex.Message));
                            }
                        }
                    }
                }

                Log("[MENU] All menu callbacks registered successfully");
            }
            catch (Exception ex)
            {
                Log(string.Format("[MENU] Registration failed: {0}\n{1}", ex.Message, ex.StackTrace));
            }
        }

        // ================================================================
        // Application lifecycle callbacks
        // ================================================================

        private static int OnAppInit()
        {
            Log("[APP] ApplicationInit callback");
            return 0;
        }

        private static int OnAppEnter()
        {
            Log("[APP] ApplicationEnter callback");
            return 0;
        }

        private static int OnAppExit()
        {
            Log("[APP] ApplicationExit callback");
            return 0;
        }

        // ================================================================
        // Menu action callbacks — CallbackStatus(CallbackStatus.MenuButtonEvent)
        // Continue=0 means normal processing
        // ================================================================

        // NX2412: MenuBar.MenuBarManager and MenuButtonEvent removed. Use dynamic dispatch.
        private static int OnAskAiAction(dynamic buttonEvent)
        {
            try { if (_contextHandler != null) _contextHandler.OnAskAi(); }
            catch (Exception ex) { Log(string.Format("[AskAI] {0}", ex.Message)); }
            return 0; // Cancel
        }

        private static int OnToggleServerAction(dynamic buttonEvent)
        {
            try { ToggleServer(); }
            catch (Exception ex) { Log(string.Format("[StartSrv] {0}", ex.Message)); }
            return 0; // Cancel
        }

        private static int OnShowStatusAction(dynamic buttonEvent)
        {
            try { ShowStatus(); }
            catch (Exception ex) { Log(string.Format("[Status] {0}", ex.Message)); }
            return 0; // Cancel
        }

        // Object-wrapper versions for Delegate.CreateDelegate contravariance bridge
        private static int OnAskAiActionObj(object buttonEvent) {
            Log("[BTN] Ask AI button clicked! buttonEvent type=" + (buttonEvent != null ? buttonEvent.GetType().FullName : "NULL"));
            return OnAskAiAction((dynamic)buttonEvent);
        }
        private static int OnToggleServerActionObj(object buttonEvent) {
            Log("[BTN] Toggle Server button clicked!");
            return OnToggleServerAction((dynamic)buttonEvent);
        }
        private static int OnShowStatusActionObj(object buttonEvent) {
            Log("[BTN] Show Status button clicked!");
            return OnShowStatusAction((dynamic)buttonEvent);
        }
        private static int OnHoverCaptureActionObj(object buttonEvent) {
            Log("[BTN] Hover Capture button clicked!");
            if (_contextHandler != null) _contextHandler.OnHoverCapture();
            return 0;
        }

        public static int Shutdown()
        {
            Log("Plugin shutting down...");
            MainThreadDispatcher.Shutdown();
            if (_monitor != null) _monitor.Stop();
            if (_server != null) _server.Stop();
            if (_serverThread != null) _serverThread.Join(TimeSpan.FromSeconds(3));
            Log("Plugin closed");
            return 0;
        }

        public static int GetUnloadOption(string arg)
        {
            // Programs that register callbacks must use AtTermination
            // Return 3 = Session.LibraryUnloadOption.AtTermination
            return 3;
        }

        private static void Log(string msg)
        {
            try
            {
                Console.WriteLine(string.Format("[NX-MCP-Plugin] {0:HH:mm:ss} {1}", DateTime.Now, msg));
                // Also write to file log for debugging
                var logPath = NxPaths.StartupFile("tcp_server.log");
                System.IO.File.AppendAllText(logPath,
                    string.Format("[{0:HH:mm:ss.fff}] [Plugin] {1}\n", DateTime.Now, msg));
            }
            catch { /* silent */ }
        }

        /// <summary>
        /// Write a non-rotating status snapshot to disk.
        /// Unlike tcp_server.log, this file is never rotated — diagnostics can
        /// always read it to get the last successful startup state, even after
        /// log rotation swallows the startup block from tcp_server.log.
        /// </summary>
        private static void WriteStatusSnapshot(string host, int port)
        {
            try
            {
                var registry = ToolRegistry.Instance;
                int toolCount = registry != null ? registry.ToolCount : 0;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendFormat("  \"startup_time\": \"{0:O}\",\n", DateTime.Now);
                sb.AppendFormat("  \"plugin_version\": \"{0}\",\n", PluginVersion.Full);
                sb.AppendFormat("  \"tool_count\": {0},\n", toolCount);
                sb.AppendFormat("  \"tcp_host\": \"{0}\",\n", host);
                sb.AppendFormat("  \"tcp_port\": {0},\n", port);
                sb.AppendFormat("  \"menu_registered\": true,\n");
                sb.AppendFormat("  \"nx_version\": \"{0}\",\n",
                    System.IO.Path.GetFileName(
                        System.IO.Path.GetDirectoryName(
                            System.IO.Path.GetDirectoryName(NxPaths.StartupDir))) ?? "unknown");
                sb.AppendFormat("  \"dotnet_version\": \"{0}\"\n",
                    System.Environment.Version.ToString());
                sb.AppendLine("}");
                var statusPath = NxPaths.StartupFile("nx_mcp_status.json");
                System.IO.File.WriteAllText(statusPath, sb.ToString());
                Log(string.Format("Status snapshot written: {0}", statusPath));
            }
            catch (Exception ex)
            {
                Log(string.Format("Status snapshot write failed: {0}", ex.Message));
            }
        }

        /// <summary>
        /// AssemblyResolve event handler: load missing assemblies from NXBIN/managed/
        /// Solves the issue where NX cannot find lazy-loaded assemblies like Newtonsoft.Json
        /// </summary>
        private static System.Reflection.Assembly ResolveNxAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                // Parse AssemblyName, e.g. "Newtonsoft.Json, Version=13.0.0.0, ..."
                var name = new System.Reflection.AssemblyName(args.Name).Name;

                // Candidate paths — don't hardcode install directory (hardcoded = Newtonsoft.Json resolution fails on non-default install machines)
                // NX install root by priority: UGII_BASE_DIR (set by running NX itself, most accurate)
                //                           → NX_ROOT (user explicit override) → %ProgramFiles%\Siemens\NX* (fallback scan)
                var candidates = new System.Collections.Generic.List<string>();
                foreach (var root in NxInstallRoots())
                {
                    AddCandidate(candidates, System.IO.Path.Combine(root, "NXBIN", "managed", name + ".dll"));
                    AddCandidate(candidates, System.IO.Path.Combine(root, "UGII", "managed", name + ".dll"));
                }

                foreach (var path in candidates)
                {
                    if (System.IO.File.Exists(path))
                    {
                        Log(string.Format("AssemblyResolve: Loading {0} from {1}", name, path));
                        return System.Reflection.Assembly.LoadFrom(path);
                    }
                }

                Log(string.Format("AssemblyResolve: Not found {0}", args.Name));
                return null;
            }
            catch (Exception ex)
            {
                Log(string.Format("AssemblyResolve exception: {0}", ex.Message));
                return null;
            }
        }

        /// <summary>
        /// NX install root candidate list (deduplicated by priority):
        ///   1. UGII_BASE_DIR — set by running NX itself, this is the current NX instance
        ///   2. NX_ROOT      — user explicit override
        ///   3. %ProgramFiles%\Siemens\NX* — fallback scan when neither is set (higher version first)
        /// </summary>
        /// <summary>
        /// NX install root candidates (higher version first) — implemented in NxPaths, shared across modules with the same detection rules.
        /// </summary>
        private static System.Collections.Generic.List<string> NxInstallRoots()
        {
            return NxPaths.InstallRoots();
        }

        /// <summary>Add a candidate assembly path (case-insensitive deduplication — avoid duplicate attempts when directories overlap).</summary>
        private static void AddCandidate(System.Collections.Generic.List<string> candidates, string path)
        {
            foreach (var existing in candidates)
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return;
            candidates.Add(path);
        }
    }
}
