using System;
using System.IO;

namespace NxMcpPlugin
{
    /// <summary>
    /// Plugin version — single source of truth for the entire package.
    ///
    /// ★ Do not write version number literals anywhere else. Change version only here.
    ///   (Historically there were simultaneously v0.3.1-fixed / v0.3.2 / v0.3.2-edge-midpoint / 0.74.0-37768dc
    ///    four different version strings, where the last one was even a private repo commit hash that doesn't even exist in this package.)
    /// </summary>
    internal static class PluginVersion
    {
        /// <summary>Version number (semantic version).</summary>
        public const string Number = "0.4.0";

        /// <summary>Full version string displayed in UI.</summary>
        public static string Full { get { return Number; } }
    }

    /// <summary>
    /// NX user directory resolution.
    ///
    /// Log and request files fall under NX's **user-level** directory:
    ///     %LOCALAPPDATA%\Siemens\&lt;version&gt;\startup\
    /// Where &lt;version&gt; varies with installed NX version (NX2412 / NX2506 / ...).
    ///
    /// ★ Do not hardcode "NX2412" in any path — on other NX versions,
    ///   File.AppendAllText will throw exception due to non-existent directory,
    ///   and callers generally silently swallow with try/catch,
    ///   resulting in **zero logs, zero errors**, completely blind when troubleshooting.
    ///
    /// Resolution order:
    ///   1. UGII_BASE_DIR set by running NX itself (most accurate — it's the current NX instance)
    ///   2. NX_ROOT set by user
    ///   3. Scan %LOCALAPPDATA%\Siemens\NX&lt;digits&gt; and take highest version
    ///   4. Fallback to %LOCALAPPDATA%\Siemens\startup
    ///
    /// ⚠️ Step 3 only recognizes directory names with "NX" followed by pure digits. Must be strict here —
    ///    that directory also contains unrelated directories like nxsl / San, and loose matching
    ///    ("starts with NX") would incorrectly include them.
    /// </summary>
    internal static class NxPaths
    {
        private static string _startupDir;

        /// <summary>Startup subdirectory under NX user directory.</summary>
        public static string StartupDir
        {
            get
            {
                if (_startupDir == null) _startupDir = ResolveStartupDir();
                return _startupDir;
            }
        }

        /// <summary>Full path of a file under startup directory.</summary>
        public static string StartupFile(string fileName)
        {
            return Path.Combine(StartupDir, fileName);
        }

        private static string ResolveStartupDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string siemens = Path.Combine(local, "Siemens");

            string ver = VersionFolderName(Environment.GetEnvironmentVariable("UGII_BASE_DIR"));
            if (ver == null)
                ver = VersionFolderName(Environment.GetEnvironmentVariable("NX_ROOT"));
            if (ver != null)
                return Path.Combine(siemens, ver, "startup");

            string scanned = ScanHighestVersionFolder(siemens);
            if (scanned != null)
                return Path.Combine(siemens, scanned, "startup");

            return Path.Combine(siemens, "startup");
        }

        /// <summary>Get version directory name from NX install root directory name (e.g. ...\NX2412 → "NX2412"); return null if it doesn't look like an NX version.</summary>
        private static string VersionFolderName(string nxRoot)
        {
            if (string.IsNullOrEmpty(nxRoot)) return null;
            try
            {
                string name = Path.GetFileName(nxRoot.TrimEnd('\\', '/'));
                return IsNxVersionFolder(name) ? name : null;
            }
            catch { return null; }
        }

        /// <summary>Whether directory name is "NX" + pure digits. Intentionally strict to avoid misselecting unrelated directories like nxsl.</summary>
        private static bool IsNxVersionFolder(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3) return false;
            if (name[0] != 'N' && name[0] != 'n') return false;
            if (name[1] != 'X' && name[1] != 'x') return false;
            for (int i = 2; i < name.Length; i++)
                if (!char.IsDigit(name[i])) return false;
            return true;
        }

        private static string ScanHighestVersionFolder(string siemens)
        {
            try
            {
                if (!Directory.Exists(siemens)) return null;
                string best = null;
                foreach (string dir in Directory.GetDirectories(siemens))
                {
                    string name = Path.GetFileName(dir);
                    if (!IsNxVersionFolder(name)) continue;
                    if (best == null || CompareVersionNames(name, best) > 0) best = name;
                }
                return best;
            }
            catch { return null; }
        }

        private static int CompareVersionNames(string a, string b)
        {
            long na = 0, nb = 0;
            long.TryParse(a.Substring(2), out na);
            long.TryParse(b.Substring(2), out nb);
            return na.CompareTo(nb);
        }

        // ================================================================
        // NX install root (different from "user directory" above — this is the install under Program Files)
        // ================================================================

        /// <summary>
        /// NX install root candidates, sorted by priority:
        ///   UGII_BASE_DIR (set by running NX itself, most accurate) → NX_ROOT (user explicitly specified)
        ///   → %ProgramFiles%\Siemens\NX&lt;digits&gt; (higher versions first)
        /// Trailing slashes removed and case-insensitively deduplicated (overlapping roots won't be probed repeatedly).
        ///
        /// ★ Do not hardcode "C:\Program Files\Siemens\NX2412" anywhere — will fail on machines where it's installed elsewhere.
        /// </summary>
        public static System.Collections.Generic.List<string> InstallRoots()
        {
            var roots = new System.Collections.Generic.List<string>();

            AddRoot(roots, Environment.GetEnvironmentVariable("UGII_BASE_DIR"));
            AddRoot(roots, Environment.GetEnvironmentVariable("NX_ROOT"));

            try
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (string.IsNullOrEmpty(programFiles))
                    programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
                string siemens = Path.Combine(programFiles, "Siemens");
                if (!Directory.Exists(siemens)) return roots;

                // Collect all NX<digit> directories (excluding unrelated directories like San), sorted by version descending — higher versions first
                var vers = new System.Collections.Generic.List<string>();
                foreach (string dir in Directory.GetDirectories(siemens))
                {
                    string name = Path.GetFileName(dir);
                    if (IsNxVersionFolder(name)) vers.Add(name);
                }
                vers.Sort(CompareVersionNames);
                vers.Reverse();
                foreach (string v in vers) AddRoot(roots, Path.Combine(siemens, v));
            }
            catch { /* Skip if scan fails — still have UGII_BASE_DIR / NX_ROOT candidates */ }

            return roots;
        }

        /// <summary>Highest priority NX install root; return null when none found.</summary>
        public static string BestInstallRoot()
        {
            var roots = InstallRoots();
            return roots.Count > 0 ? roots[0] : null;
        }

        /// <summary>Path to ugraf.exe (return if exists, otherwise null).</summary>
        public static string UgrafPath()
        {
            foreach (string root in InstallRoots())
            {
                try
                {
                    string exe = Path.Combine(root, "NXBIN", "ugraf.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Add an install root (trim trailing slash; ignore empty values; case-insensitive deduplication).</summary>
        private static void AddRoot(System.Collections.Generic.List<string> roots, string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            root = root.TrimEnd('\\', '/');
            if (root.Length == 0) return;
            foreach (var existing in roots)
                if (string.Equals(existing, root, StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(root);
        }
    }
}
