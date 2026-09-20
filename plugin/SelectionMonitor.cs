using System;
using System.Collections.Generic;
using System.Linq;
using NXOpen;

namespace NxMcpPlugin
{
    /// <summary>
    /// Selection monitor — caches geometric objects selected by user in NX UI
    ///
    /// Two retrieval paths, each as fallback for the other:
    ///   1. SelectionSubscriber.RegisterOnSelectionChangeCallback  — Event push (NX11+)
    ///      Replaces the previously commented-out dynamic +=. Callback triggers after each
    ///      user action, providing incremental selected/deselected object list and clearAll
    ///      flag. This is the official C# 5-compatible API, no need for dynamic event subscription.
    ///
    ///   2. GetNumSelectedObjects() / GetSelectedTaggedObject(i)  — Active polling (fallback)
    ///      Call RefreshSelection() immediately after callback triggers to fetch complete list.
    ///      When user right-clicks "Ask AI", the right-clicked object has entered selection
    ///      state, and this API can read it.
    ///
    /// NXOpenUI.xml line number reference:
    ///   line 13686: SelectionSubscriber.OnSelectionChangeCallback delegate
    ///   line 13302: Selection.GetNumSelectedObjects()
    ///   line 13320: Selection.GetSelectedTaggedObject(int)
    /// </summary>
    public class SelectionMonitor
    {
        private List<Protocol.SelectionObject> _currentSelection = new List<Protocol.SelectionObject>();
        private readonly object _lock = new object();
        private dynamic _ui;
        // NX2412: SelectionSubscriber type not exposed. Use dynamic dispatch.
        private dynamic _subscriber;
        private bool _running = false;

        public SelectionMonitor(dynamic ui)
        {
            _ui = ui;
        }

        /// <summary>
        /// Start selection monitoring
        ///
        /// Use SelectionSubscriber (NX11+) instead of dynamic += events not supported by C# 5.
        /// NXOpenUI.xml line 13681: Created via Selection.CreateSelectionSubscriber()
        /// NXOpenUI.xml line 13720: RegisterOnSelectionChangeCallback(OnSelectionChangeCallback)
        /// </summary>
        public void Start()
        {
            LogDebug("Start() called, creating SelectionSubscriber...");
            try
            {
                // SelectionManager is a subclass of Selection, call base class method directly
                _subscriber = _ui.SelectionManager.CreateSelectionSubscriber();
                _subscriber.RegisterOnSelectionChangeCallback(
                    (dynamic)(System.Action<bool, NXOpen.TaggedObject[], NXOpen.TaggedObject[]>)OnSelectionChanged);
                _subscriber.Activate();
                _running = true;
                LogDebug("SelectionSubscriber activated");
            }
            catch (Exception ex)
            {
                LogDebug(string.Format("SelectionSubscriber creation failed (fallback to pure polling): {0}", ex.Message));
                _running = true; // Still running, but can only rely on periodic polling
            }

            // Get initial selection state
            try { RefreshSelection(); }
            catch { /* May have no selection at startup, ignore */ }
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_subscriber != null)
                {
                    _subscriber.Deactivate();
                    _subscriber.Dispose();
                    _subscriber = null;
                }
            }
            catch (Exception ex)
            {
                LogDebug(string.Format("SelectionSubscriber shutdown error: {0}", ex.Message));
            }
        }

        /// <summary>
        /// Selection change callback — called by NX SelectionSubscriber after each user action
        ///
        /// NXOpenUI.xml line 13686-13698: OnSelectionChangeCallback delegate:
        ///   void(bool clearAll, TaggedObject[] deselectedObjects, TaggedObject[] selectedObjects)
        ///
        /// clearAll=true  → All previous selections cleared, selectedObjects is the complete set of current selections
        /// clearAll=false → deselectedObjects + selectedObjects are incremental changes
        /// </summary>
        private void OnSelectionChanged(bool clearAll, TaggedObject[] deselected, TaggedObject[] selected)
        {
            lock (_lock)
            {
                if (clearAll)
                {
                    // Full set replacement — use dynamic iteration to avoid compile-time type issues with NXOpen.Tag struct
                    _currentSelection.Clear();
                    if (selected != null)
                    {
                        foreach (dynamic obj in selected)
                            _currentSelection.Add(DescribeObject(obj));
                    }
                }
                else
                {
                    // Incremental update: remove deselected — Tag converted to int via dynamic
                    if (deselected != null && deselected.Length > 0)
                    {
                        var removedTags = new HashSet<int>();
                        foreach (dynamic o in deselected)
                            removedTags.Add((int)o.Tag);
                        _currentSelection.RemoveAll(s => removedTags.Contains(s.Tag));
                    }
                    // Incremental update: add newly selected
                    if (selected != null)
                    {
                        foreach (dynamic obj in selected)
                        {
                            int tag = (int)obj.Tag;
                            if (!_currentSelection.Exists(s => s.Tag == tag))
                                _currentSelection.Add(DescribeObject(obj));
                        }
                    }
                }
            }
            LogDebug(string.Format("OnSelectionChanged: clearAll={0}, deselected={1}, selected={2}, total={3}",
                clearAll,
                deselected != null ? deselected.Length : 0,
                selected != null ? selected.Length : 0,
                _currentSelection.Count));
        }

        /// <summary>
        /// Force refresh — actively fetch complete selection list via SelectionManager API (fallback path)
        ///
        /// Note: GetNumSelectedObjects() causes memory access violation in journal context,
        /// but is available in ManagedPlugin (loaded within NX process).
        /// </summary>
        public void RefreshSelection()
        {
            lock (_lock)
            {
                try
                {
                    dynamic selMgr = _ui.SelectionManager;
                    int count = (int)selMgr.GetNumSelectedObjects();
                    if (count == 0) return; // Don't overwrite cache when there are no selected objects

                    var fresh = new List<Protocol.SelectionObject>();
                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            dynamic obj = selMgr.GetSelectedTaggedObject(i);
                            fresh.Add(DescribeObject(obj));
                        }
                        catch { /* Single object failure doesn't affect others */ }
                    }

                    LogDebug(string.Format("RefreshSelection: API reports {0} objects (cache currently has {1})",
                        count, _currentSelection.Count));
                    _currentSelection = fresh;
                }
                catch (Exception ex)
                {
                    // If GetNumSelectedObjects is unavailable (NX version differences),
                    // keep cache maintained by SelectionSubscriber, don't throw exception
                    LogDebug(string.Format("RefreshSelection failed (will use SelectionSubscriber cache): {0}", ex.Message));
                }
            }
        }

        private void LogDebug(string msg)
        {
            string line = string.Format("[{0:HH:mm:ss.fff}] [Selection] {1}", DateTime.Now, msg);
            // Dual write: console (visible in NX System Log) + file
            try { Console.WriteLine(line); } catch { }
            try
            {
                var logPath = NxPaths.StartupFile("tcp_server.log");
                System.IO.File.AppendAllText(logPath, line + "\n");
            }
            catch { }
        }

        /// <summary>
        /// Get current selection list
        ///
        /// Strategy: return cache maintained by SelectionSubscriber (event-driven),
        /// then asynchronously trigger RefreshSelection to stay in sync.
        /// </summary>
        public List<Protocol.SelectionObject> GetSelection()
        {
            // Call RefreshSelection first as synchronous verification (this API is available in plugin context)
            try { RefreshSelection(); }
            catch { /* fall through to cached list */ }

            lock (_lock)
            {
                return new List<Protocol.SelectionObject>(_currentSelection);
            }
        }

        /// <summary>
        /// Convert NX TaggedObject to SelectionObject description
        /// Accepts dynamic or TaggedObject, compatible with both call paths
        /// </summary>
        private Protocol.SelectionObject DescribeObject(dynamic obj)
        {
            var desc = new Protocol.SelectionObject();
            try
            {
                desc.Tag = (int)obj.Tag;
                desc.Type = obj.GetType().Name;

                // Face
                if (desc.Type == "Face")
                {
                    desc.FaceType = obj.SolidFaceType != null ? obj.SolidFaceType.ToString() : "Unknown";
                    try { desc.Area = (double)obj.GetArea(); } catch { }
                    try
                    {
                        var normal = (double[])obj.GetNormal();
                        desc.Normal = normal;
                    }
                    catch { }
                    try
                    {
                        var body = obj.GetBody();
                        desc.BodyName = body != null ? body.JournalIdentifier : "?";
                    } catch { }
                }
                // Edge
                else if (desc.Type == "Edge")
                {
                    try { desc.Length = (double)obj.GetLength(); } catch { }
                    try
                    {
                        var body = obj.GetBody();
                        desc.BodyName = body != null ? body.JournalIdentifier : "?";
                    } catch { }
                }
                // Body
                else if (desc.Type == "Body")
                {
                    desc.BodyType = obj.IsSolid ? "Solid" : "Sheet";
                    desc.Name = obj.JournalIdentifier != null ? obj.JournalIdentifier : "Unnamed";
                    try { desc.FaceCount = ((dynamic[])obj.GetFaces()).Length; } catch { }
                    try { desc.EdgeCount = ((dynamic[])obj.GetEdges()).Length; } catch { }
                }
            }
            catch { /* Skip when getting properties fails */ }

            return desc;
        }
    }
}
