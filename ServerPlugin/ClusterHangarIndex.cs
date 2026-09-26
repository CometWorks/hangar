using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml.Serialization;

namespace ServerPlugin;

// Operational state stays outside PluginSdk configuration: cluster nodes must keep the same canonical config.
public sealed class HangarPlayerState
{
    public List<HangarEntry> Entries { get; set; } = new List<HangarEntry>();
    public string LastSaveUtc { get; set; }
}

internal sealed class ClusterHangarIndex
{
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(HangarPlayerState));
    private readonly ConcurrentDictionary<ulong, object> localLocks = new ConcurrentDictionary<ulong, object>();
    private readonly string players;

    internal string Root { get; }

    internal ClusterHangarIndex(string root)
    {
        Root = root;
        players = Path.Combine(root, "Players");
        Directory.CreateDirectory(players);
    }

    private string PathFor(ulong steamId) => Path.Combine(players, steamId.ToString(CultureInfo.InvariantCulture) + ".xml");

    internal HangarPlayerState Read(ulong steamId)
    {
        var path = PathFor(steamId);
        if (!File.Exists(path)) return new HangarPlayerState();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return (HangarPlayerState)Serializer.Deserialize(file);
    }

    internal (int Entries, int Cooldowns) Counts()
    {
        int entries = 0, cooldowns = 0;
        foreach (var path in Directory.EnumerateFiles(players, "*.xml"))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var state = (HangarPlayerState)Serializer.Deserialize(file);
            entries += state.Entries.Count;
            if (!string.IsNullOrWhiteSpace(state.LastSaveUtc)) cooldowns++;
        }
        return (entries, cooldowns);
    }

    internal Lease Acquire(ulong steamId)
    {
        var sync = localLocks.GetOrAdd(steamId, _ => new object());
        Monitor.Enter(sync);
        FileStream file = null;
        try
        {
            file = new FileStream(PathFor(steamId) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            var wait = Stopwatch.StartNew();
            while (true)
            {
                try { file.Lock(0, 1); break; }
                catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(25); }
            }
            return new Lease(sync, file, PathFor(steamId), Read(steamId));
        }
        catch
        {
            file?.Dispose();
            Monitor.Exit(sync);
            throw;
        }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly object sync;
        private readonly FileStream file;
        private readonly string path;
        internal HangarPlayerState State { get; }

        internal Lease(object sync, FileStream file, string path, HangarPlayerState state)
        { this.sync = sync; this.file = file; this.path = path; State = state; }

        internal void Save()
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { Serializer.Serialize(output, State); output.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public void Dispose()
        {
            try { file.Unlock(0, 1); }
            finally { file.Dispose(); Monitor.Exit(sync); }
        }
    }
}
