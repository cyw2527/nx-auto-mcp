using System;
using NXOpen;

namespace NxMcpPlugin.Features
{
    /// <summary>
    /// UndoMark scope manager — RAII pattern wrapping NXOpen UndoMark transactions.
    ///
    /// NXOpen API requires explicitly creating UndoMark and committing or rolling back after operation completes.
    /// This class automatically manages UndoMark lifecycle via IDisposable pattern:
    /// - On success, call Commit() to mark committed; Dispose won't roll back.
    /// - On exception, Dispose() automatically rolls back to UndoMark, cleaning up residual geometry.
    ///
    /// NX API call chain:
    ///   Create: session.SetUndoMark(visibility, name)
    ///   Rollback: session.UndoToMark(markId, name) + session.DeleteUndoMark(markId, name)
    ///   Commit: mark stays in undo stack (A2/W2 2026-09-03: no longer DeleteUndoMark —
    ///         after deletion, nx_undo (UndoToLastVisibleMark) would skip all tool operations and go directly back to
    ///         the global mark from plugin load, one undo clears entire session)
    ///
    /// Usage example:
    /// <code>
    /// // Normal path: Dispose doesn't roll back after Commit
    /// using (var mark = new UndoMarkScope(session, "CreateExtrude"))
    /// {
    ///     var builder = workPart.Features.CreateExtrudeBuilder(null);
    ///     // ... set parameters ...
    ///     var feature = builder.CommitFeature();
    ///     builder.Destroy();
    ///     mark.Commit();  // Success: marked as committed, Dispose doesn't execute rollback
    /// }
    ///
    /// // Exception path: no Commit, Dispose auto-rolls back
    /// using (var mark = new UndoMarkScope(session, "CreateExtrude"))
    /// {
    ///     var builder = workPart.Features.CreateExtrudeBuilder(null);
    ///     // ... set parameters ...
    ///     var feature = builder.CommitFeature();  // Throws exception
    ///     builder.Destroy();
    ///     mark.Commit();
    /// }
    /// // During exception propagation, Dispose auto-calls UndoToMark + DeleteUndoMark
    /// </code>
    /// </summary>
    public sealed class UndoMarkScope : IDisposable
    {
        /// <summary>Thread synchronization lock, protects concurrent access to _activeMarks counter</summary>
        private static readonly object _lock = new object();

        /// <summary>Current active UndoMark count (thread-safe)</summary>
        private static int _activeMarks = 0;

        /// <summary>NX UndoMark maximum concurrency limit. NX may exhibit unpredictable behavior beyond this count.</summary>
        public const int MaxMarks = 10;

        private readonly dynamic _session;
        private readonly string _markName;
        private readonly dynamic _markId;
        private bool _committed;
        private bool _rolledBack;
        private bool _disposed;

        public UndoMarkScope(dynamic session, string markName)
        {
            if (session == null)
                throw new ArgumentNullException("session", "NXOpen Session cannot be null");
            if (string.IsNullOrEmpty(markName))
                throw new ArgumentException("UndoMark name cannot be empty", "markName");

            lock (_lock)
            {
                if (_activeMarks >= MaxMarks)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "UndoMark active count has reached limit {0} (current: {1}). " +
                            "Please check for unreleased UndoMarkScope instances, " +
                            "or reduce nested UndoMark usage.",
                            MaxMarks, _activeMarks));
                }
                _activeMarks++;
            }

            _session = session;
            _markName = markName;
            _markId = session.SetUndoMark(NXOpen.Session.MarkVisibility.Visible, markName);
        }

        public bool IsCommitted
        {
            get { return _committed; }
        }

        public int MarkId
        {
            get { return Convert.ToInt32(_markId); }
        }

        public void Commit()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName,
                    "UndoMarkScope has been disposed, cannot call Commit() at this time");

            if (_committed || _rolledBack)
                return;

            _committed = true;
        }

        public void Rollback()
        {
            if (_disposed || _committed || _rolledBack)
                return;

            _rolledBack = true;

            try
            {
                _session.UndoToMark(_markId, _markName);
            }
            catch
            {
            }

            try
            {
                _session.DeleteUndoMark(_markId, _markName);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (!_committed && !_rolledBack)
            {
                Rollback();
            }

            lock (_lock)
            {
                _activeMarks--;
            }
        }
    }
}
