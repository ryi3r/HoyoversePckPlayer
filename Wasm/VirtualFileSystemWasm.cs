using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HoyoversePckPlayer;

public class VirtualFileSystemWasm : IDisposable
{
    [Flags]
    public enum FileStatus
    {
        ReadReady = 1 << 0,
        WriteReady = 1 << 1,
        Error = 1 << 2,
    }

    [Flags]
    public enum OpenFileFlags
    {
        Create = 64,
        CreateUnique = 128,
        Truncate = 512,
    }

    public class File(MemoryStream data)
    {
        public FileStatus FileStatus = FileStatus.ReadReady | FileStatus.WriteReady;
        public Lock Lock = new();
        public bool CanRead = true;
        public bool CanWrite = true;
        public bool Tty;
        public int Flags;
        public MemoryStream? Data = data;
        public string? Path;
    }

    readonly Lock _lock = new();
    readonly Dictionary<int, File> _openHandles = [];
    readonly Dictionary<string, MemoryStream> _internalFilesystem = [];
    public bool IgnoreInternalPipes;

    public VirtualFileSystemWasm(bool initializePipes)
    {
        if (!initializePipes)
            return;
        // init stdin / stdout / stderr   
        // stdin
        _openHandles[0] = new(new())
        {
            Tty = true,
            CanWrite = false,
        };
        _openHandles[0].Flags &= (int)~FileStatus.WriteReady;
        // stdout
        _openHandles[1] = new(new())
        {
            Tty = true,
            CanRead = false,
        };
        _openHandles[1].Flags &= (int)~FileStatus.ReadReady;
        // stderr
        _openHandles[2] = new(new())
        {
            Tty = true,
            CanRead = false,
        };
        _openHandles[2].Flags &= (int)~FileStatus.ReadReady;
    }
    
    public FileStatus? GetOpenFileStatus(int handle)
    {
        lock (_lock)
            return _openHandles.TryGetValue(handle, out var v) ? v.FileStatus : null;
    }
    
    public int? GetOpenFileFlags(int handle)
    {
        lock (_lock)
            return _openHandles.TryGetValue(handle, out var v) ? v.Flags : null;
    }
    
    public void SetOpenFileFlags(int handle, int flags)
    {
        lock (_lock)
        {
            // ReSharper disable once NotAccessedVariable
            if (_openHandles.TryGetValue(handle, out var v))
                v.Flags = flags;
        }
    }
    
    public long? GetOpenFilePosition(int handle)
    {
        lock (_lock)
            return _openHandles.TryGetValue(handle, out var v) ? v.Data?.Position : null;
    }
    
    public Lock? GetOpenFileLock(int handle)
    {
        lock (_lock)
            return _openHandles.TryGetValue(handle, out var v) ? v.Lock : null;
    }
    
    public MemoryStream? GetOpenFileStream(int handle)
    {
        lock (_lock)
            return _openHandles.TryGetValue(handle, out var v) ? v.Data : null;
    }

    public bool? GetOpenFileTty(int handle)
    {
        lock (_lock)
            return _openHandles.TryGetValue(handle, out var v) ? v.Tty : null;
    }
    
    public bool? GetOpenFileCanRead(int handle)
    {
        lock (_lock)
            return _openHandles.TryGetValue(handle, out var v) ? v.CanRead : null;
    }
    
    public bool? GetOpenFileCanWrite(int handle)
    {
        lock (_lock)
            return _openHandles.TryGetValue(handle, out var v) ? v.CanWrite : null;
    }

    int? GetFreeHandle(bool ensureLock = true)
    {
        if (ensureLock)
            _lock.Enter();
        for (var i = 3; i < 64; i++)
        {
            if (_openHandles.ContainsKey(i))
                continue;
            if (ensureLock)
                _lock.Exit();
            return i;
        }
        if (ensureLock)
            _lock.Exit();
        return null;
    }
    
    public int? DuplicateHandle(int handle, int? newHandle = null)
    {
        lock (_lock)
        {
            newHandle ??= GetFreeHandle(false);
            if (newHandle == null)
                return null;
            if (!_openHandles.TryGetValue(handle, out var h))
                return null;
            if (h.Data == null)
                return null;
            var stream = new MemoryStream();
            stream.Write(h.Data.ToArray());
            var o = h.Data.Position;
            h.Data.CopyTo(stream);
            h.Data.Position = o;
            stream.Position = o;
            _openHandles.Add((int)newHandle, new(stream)
            {
                Lock = h.Lock,
            });
            return newHandle;
        }
    }

    public bool IsHandleOpen(int handle)
    {
        lock (_lock)
            return _openHandles.ContainsKey(handle);
    }

    public int OpenHandle(string path, OpenFileFlags flags)
    {
        lock (_lock)
        {
            if (path.Length == 0)
                return -44;
            if ((flags & OpenFileFlags.Create) > 0)
            {
                if (_internalFilesystem.ContainsKey(path) && (flags & OpenFileFlags.CreateUnique) > 0)
                    return -20;
                if (!_internalFilesystem.ContainsKey(path))
                    _internalFilesystem.Add(path, new());
            }
            else if (!_internalFilesystem.ContainsKey(path))
                return -44;
            if ((flags & OpenFileFlags.Truncate) > 0 && (flags & OpenFileFlags.Create) == 0)
                _internalFilesystem[path].SetLength(0);
            //_internalFilesystem[path].Clear();
            //Array.Clear(_internalFilesystem[path]);
            var handle = GetFreeHandle(false);
            if (handle == null)
                return -1;
            var stream = new MemoryStream();
            var b = _internalFilesystem[path];
            b.Position = 0;
            b.CopyTo(stream);
            stream.Position = 0;
            _openHandles[(int)handle] = new(stream)
            {
                Flags = (int)flags & ~(128 | 512 | 131072),
                Path = path,
            };
            return (int)handle;
        }
    }

    public void CloseHandle(int handle)
    {
        if (IgnoreInternalPipes && handle is >= 0 and <= 2)
            return;
        lock (_lock)
        {
            if (!_openHandles.TryGetValue(handle, out var h))
                return;
            if (h is { Path: not null, Data: not null })
                _internalFilesystem[h.Path!] = h.Data;
            else
                h.Data?.Dispose();
            h.Data = null;
            _openHandles.Remove(handle);
        }
    }

    public void InsertResource(string path, byte[] data)
    {
        var stream = new MemoryStream();
        stream.Write(data);
        lock (_lock)
            _internalFilesystem.Add(path, stream);
    }
    
    public void ClearHandles(bool includeInternal = false)
    {
        lock (_lock)
        {
            foreach (var handle in _openHandles.Keys.ToArray())
            {
                if (!includeInternal && handle is >= 0 and <= 2)
                    continue;
                _openHandles[handle].Data?.Dispose();
                _openHandles.Remove(handle);
            }
        }
    }

    public void ClearResources()
    {
        lock (_lock)
        {
            foreach (var s in _internalFilesystem.Values)
                s.Dispose();
            _internalFilesystem.Clear();
        }
    }
    
    public void RemoveResource(string path)
    {
        lock (_lock)
        {
            if (!_internalFilesystem.TryGetValue(path, out var v))
                return;
            v.Dispose();
            _internalFilesystem.Remove(path);
        }
    }
    
    public byte[]? FetchResource(string path)
    {
        lock (_lock)
        {
            if (_internalFilesystem.TryGetValue(path, out var v))
                return v.ToArray();
        }
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var h in _openHandles.Values)
            {
                lock (h.Lock)
                    h.Data?.Dispose();
            }
            _openHandles.Clear();
        }
        foreach (var (_, v) in _internalFilesystem)
            v.Dispose();
        _internalFilesystem.Clear();
        GC.SuppressFinalize(this);
    }
}
