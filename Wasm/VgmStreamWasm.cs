using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Wasmtime;
using Module = Wasmtime.Module;

namespace HoyoversePckPlayer;

public class VgmStreamWasm : IDisposable
{
    readonly Engine _engine = new(new Config()
        .WithSIMD(true)
        .WithBulkMemory(true)
        .WithMultiMemory(true)
        .WithMultiValue(true)
        .WithReferenceTypes(true)
        .WithWasmThreads(true)
        .WithProfilingStrategy(ProfilingStrategy.None)
        .WithOptimizationLevel(OptimizationLevel.Speed)
    );
    readonly Module _module;
    readonly Linker _linker;
    readonly Store _store;
    Instance _inst;
    readonly Memory _mem;
    public VirtualFileSystemWasm Fs;
    public readonly Dictionary<string, string> Env = [];
    public bool Closed;
    public int CloseCode;

    public VgmStreamWasm(bool initializeFsPipes)
    {
        Fs = new(initializeFsPipes);
        //Env.Add("USER", "user");
        //Env.Add("LOGNAME", "user");
        Env.Add("PATH", "/");
        Env.Add("PWD", "/");
        Env.Add("HOME", "/home/web_user");
        Env.Add("LANG", "en_US.UTF-8");
        Env.Add("_", "vgmstream");
        {
            var asm = Assembly.GetExecutingAssembly();
            var s = asm.GetManifestResourceStream($"{asm.GetName().Name}.vgmstream.vgmstream-cli.wasm")!;
            _module = Module.FromStream(_engine, "vgmstream-cli.wasm", s);
            s.Dispose();
        }
        _linker = new(_engine);
        _store = new(_engine);
        #region Define WASM external functions
        // ___assert_fail
        _linker.DefineFunction("a", "c", (int cond, int fName, int line, int func) => throw new InvalidOperationException($"Assertion failed: {PointerToValidString(cond)}, at: {PointerToValidString(fName)} {line} {PointerToValidString(func)}"));
        // ___syscall__newselect, todo: check if this works
        _linker.DefineFunction("a", "q", (int nfds, int readfds, int writefds, int exceptfds, int _) =>
        {
            // last arg is timeout, ignore
            var read = 0ul;
            var write = 0ul;
            var except = 0ul;
            var memRead = readfds != IntPtr.Zero ? unchecked((ulong)_mem!.ReadInt64(readfds)) : 0;
            var memWrite = writefds != IntPtr.Zero ? unchecked((ulong)_mem!.ReadInt64(writefds)) : 0;
            var memExcept = exceptfds != IntPtr.Zero ? unchecked((ulong)_mem!.ReadInt64(exceptfds)) : 0;
            for (var i = 0; i < nfds; i++)
            {
                var mask = 1ul << i;
                if (((memRead | memWrite | memExcept) & mask) == 0)
                    continue;
                var status = Fs.GetOpenFileStatus(i);
                if ((status & VirtualFileSystemWasm.FileStatus.ReadReady) != 0 && (memRead & mask) > 0)
                    read |= mask;
                if ((status & VirtualFileSystemWasm.FileStatus.WriteReady) != 0 && (memWrite & mask) > 0)
                    write |= mask;
                if ((status & VirtualFileSystemWasm.FileStatus.Error) != 0 && (memExcept & mask) > 0)
                    except |= mask;
            }
            if (readfds != IntPtr.Zero)
                _mem!.WriteInt64(readfds, unchecked((long)read));
            if (writefds != IntPtr.Zero)
                _mem!.WriteInt64(writefds, unchecked((long)write));
            if (exceptfds != IntPtr.Zero)
                _mem!.WriteInt64(exceptfds, unchecked((long)except));
            return 0;
        });
        // ___syscall_dup
        _linker.DefineFunction("a", "j", (int handle) => Fs.DuplicateHandle(handle) ?? -1);
        // ___syscall_fcntl64, todo: check if this works
        _linker.DefineFunction("a", "a", (int handle, int code, int args) =>
        {
            if (args == IntPtr.Zero || !Fs.IsHandleOpen(handle))
                return -1;
            switch (code)
            {
                case 0:
                    {
                        var newHandle = _mem!.ReadInt32(args);
                        if (newHandle < 0)
                            return -28;
                        while (Fs.IsHandleOpen(newHandle))
                            newHandle++;
                        if (Fs.DuplicateHandle(handle, newHandle) == null)
                            return -28;
                        return newHandle;
                    }
                case 1 or 2:
                    return 0;
                case 3:
                    return (int)Fs.GetOpenFileFlags(handle)!;
                case 4:
                    {
                        var flags = Fs.GetOpenFileFlags(handle);
                        if (flags == null)
                            return -28;
                        Fs.SetOpenFileFlags(handle, (int)flags | _mem!.ReadInt32(args));
                    }
                    return 0;
                case 12:
                    _mem!.WriteInt16(args, 2);
                    return 0;
                case 13 or 14:
                    return 0;
            }
            var dup = Fs.DuplicateHandle(handle);
            if (dup != null)
                return (int)dup;
            return -1;
        });
        // ___syscall_ioctl
        _linker.DefineFunction("a", "l", (int handle, int code, int argPtr) =>
        {
            if (!Fs.IsHandleOpen(handle))
                return -1; 
            switch (code)
            {
                case 21523:
                    if (!(Fs.GetOpenFileTty(handle) ?? false))
                        return -59;
                    _mem!.WriteInt16(argPtr, 24);
                    _mem.WriteInt16(argPtr + 2, 80);
                    return 0;
                case 21505: // todo: check if this works
                    {
                        if (!(Fs.GetOpenFileTty(handle) ?? false))
                            return -59;
                        var tPtr = _mem!.ReadInt32(argPtr);
                        _mem.WriteInt32(tPtr, 25856);
                        _mem.WriteInt32(tPtr + 4, 5);
                        _mem.WriteInt32(tPtr + 8, 191);
                        _mem.WriteInt32(tPtr + 12, 35387);
                        foreach (var (i, n) in new byte[] { 3, 28, 127, 21, 4, 0, 1, 0, 17, 19, 26, 0, 18, 15, 23, 22, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }.Index())
                            _mem.WriteByte(tPtr + 17 + i, n);
                        return 0;
                    }
                case 21509 or 21510 or 21511 or 21512 or 21506 or 21507 or 21508 or 21519 or 21520 or 21531 or 21524 or 21515:
                    return -59;
            }
            return -28;
        });
        // ___syscall_openat
        _linker.DefineFunction("a", "g", (int dirPtr, int pathPtr, int flags, int _) =>
        {
            // ignore vars, not needed here
            var dir = dirPtr == -100 ? "" : PointerToString(dirPtr, null);
            var path = PointerToString(pathPtr, null);
            if (dir == null || path == null)
                return -1;
            while (dir.EndsWith("/..", StringComparison.InvariantCulture) || dir.EndsWith("/.", StringComparison.InvariantCulture))
            {
                if (dir.EndsWith("/../", StringComparison.InvariantCulture))
                {
                    dir = dir[..^4];
                    dir = dir[..(dir.LastIndexOf('/') + 1)];
                }
                else if (dir.EndsWith("/./", StringComparison.InvariantCulture))
                    dir = dir[..^2];
            }
            return Fs.OpenHandle($"{dir}{path}".Replace("//", "/", StringComparison.InvariantCulture), (VirtualFileSystemWasm.OpenFileFlags)flags);
        });
        // __abort_js
        _linker.DefineFunction("a", "k", () => throw new("wasm aborted"));
        // __emscripten_memcpy_js
        _linker.DefineFunction("a", "i", (int dest, int src, int size) =>
        {
            if (_mem!.GetLength() < dest + size)
                _mem.Grow(_mem.GetLength() - (dest + size));
            _mem.GetSpan(src, size).CopyTo(_mem.GetSpan(dest, size));
        });
        // __gmtime_js, todo: check if this works
        _linker.DefineFunction("a", "m", (int low, int high, int time) =>
        {
            var timeOffset = DateTimeOffset.FromUnixTimeSeconds((long)_mem!.ReadInt32(high) << 32 | (uint)_mem.ReadInt32(low));
            _mem.WriteInt32(time, timeOffset.Second);
            _mem.WriteInt32(time + 4, timeOffset.Minute);
            _mem.WriteInt32(time + 8, timeOffset.Hour);
            _mem.WriteInt32(time + 12, timeOffset.Day);
            _mem.WriteInt32(time + 16, timeOffset.Month);
            _mem.WriteInt32(time + 20, timeOffset.Year - 1900);
            _mem.WriteInt32(time + 24, (int)timeOffset.DayOfWeek);
            _mem.WriteInt32(time + 28, timeOffset.DayOfYear);
        });
        // __tzset_js
        _linker.DefineFunction("a", "r", (int _, int _, int _, int _) => // timezone, daylight, std_name, dst_name
        {
            // todo
            // ReSharper disable once ConvertToLambdaExpression
        });
        // _clock_time_get, todo: check if this works
        _linker.DefineFunction("a", "o", (int clockId, int _, int _, int timePtr) =>
        {
            if (clockId is < 0 or > 3)
                return -28;
            _mem!.WriteInt64(timePtr, clockId == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 : new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUnixTimeMilliseconds() * 1000);
            return 0;
        });
        // _emscripten_date_now
        _linker.DefineFunction("a", "v", () => (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // _emscripten_get_now
        _linker.DefineFunction("a", "f", () => (double)new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUnixTimeMilliseconds());
        // _emscripten_resize_heap
        _linker.DefineFunction("a", "p", (int requestedSize) =>
        {
            if (requestedSize > 0 && requestedSize > _mem!.GetLength())
                _mem.Grow((requestedSize - _mem.GetLength() + 0xffff) / 0xffff);
            return 0;
        });
        // _environ_get
        _linker.DefineFunction("a", "t", (int envPtr, int envBufPtr) =>
        {
            var bufSize = 0;
            foreach (var (i, (key, value)) in Env.Index())
            {
                var s = $"{key}={value}";
                var ptr = envBufPtr + bufSize;
                _mem!.WriteIntPtr(envPtr + i * 4, ptr);
                var w = _mem.WriteString(ptr, s);
                _mem.WriteByte(ptr + w, 0);
                bufSize += w + 1;
            }
            return 0;
        });
        // _environ_sizes_get
        _linker.DefineFunction("a", "u", (int envPtr, int envBufPtr) =>
        {
            _mem!.WriteInt32(envPtr, Env.Count);
            var bufSize = 0;
            foreach (var (key, value) in Env)
                bufSize += key.Length + value.Length + 2;
            _mem.WriteInt32(envBufPtr, bufSize);
            return 0;
        });
        // _exit, todo: check if this works
        _linker.DefineFunction("a", "h", (int code) =>
        {
            Closed = true;
            CloseCode = code;
        });
        // _fd_close
        _linker.DefineFunction("a", "b", (int handle) =>
        {
            Fs.CloseHandle(handle);
            return 0;
        });
        // _fd_fdstat_get, todo: check if this works
        _linker.DefineFunction("a", "s", (int handle, int bufPtr) =>
        {
            if (!Fs.IsHandleOpen(handle))
                return -1;
            _mem!.WriteByte(bufPtr, 4); // 4 == file
            _mem.WriteInt16(bufPtr + 1, (short)(Fs.GetOpenFileFlags(handle) ?? 0));
            _mem.WriteInt64(bufPtr + 3, 0);
            _mem.WriteInt64(bufPtr + 11, 0);
            return 0;
        });
        // _fd_read
        _linker.DefineFunction("a", "e", (int handle, int bufPtr, int size, int offset) =>
        {
            if (!Fs.IsHandleOpen(handle) || !(Fs.GetOpenFileCanRead(handle) ?? false) || offset < 0)
                return -1;
            var w = 0;
            for (var i = 0; i < size; i++)
            {
                var ptr = _mem!.ReadInt32(bufPtr);
                var len = _mem.ReadInt32(bufPtr + 4);
                bufPtr += 8;
                if (len <= 0)
                    continue;
                lock (Fs.GetOpenFileLock(handle)!)
                {
                    var data = Fs.GetOpenFileStream(handle);
                    if (data == null || data.Position >= data.Length)
                        break;
                    var span = _mem.GetSpan(ptr, len);
                    len = Math.Min((int)data.Position + len, (int)data.Length) - (int)data.Position;
                    data.ReadAtLeast(span, len);
                    w += len;
                }
            }
            _mem!.WriteInt32(offset, w);
            return w == 0 ? -6 : 0;
        });
        // _fd_seek
        _linker.DefineFunction("a", "n", (int handle, int low, int high, int whence, int offset) =>
        {
            if (!Fs.IsHandleOpen(handle))
                return -1;
            var fsOffset = (long)high << 32 | (uint)low;
            switch (whence)
            {
                case 0:
                    break;
                case 1:
                    fsOffset += (long)Fs.GetOpenFilePosition(handle)!;
                    break;
                case 2:
                    fsOffset += Fs.GetOpenFileStream(handle)!.Length;
                    break;
                default:
                    return -28;
            }
            if (fsOffset < 0)
                return -28;
            Fs.GetOpenFileStream(handle)!.Position = fsOffset;
            _mem!.WriteInt64(offset, fsOffset);
            return 0;
        });
        // _fd_write
        _linker.DefineFunction("a", "d", (int handle, int bufPtr, int size, int offset) =>
        {
            if (!Fs.IsHandleOpen(handle) || !(Fs.GetOpenFileCanWrite(handle) ?? false) || offset < 0)
                return -1;
            var w = 0;
            for (var i = 0; i < size; i++)
            {
                var ptr = _mem!.ReadInt32(bufPtr);
                var len = _mem.ReadInt32(bufPtr + 4);
                bufPtr += 8;
                lock (Fs.GetOpenFileLock(handle)!)
                {
                    var data = Fs.GetOpenFileStream(handle);
                    if (data == null)
                        return -1;
                    if (!(handle is 0 or 1 or 2 && Fs.IgnoreInternalPipes))
                        data.Write(_mem.GetSpan(ptr, len));
                }
                w += len;
            }
            _mem!.WriteInt32(offset, w);
            return 0;
        });
        #endregion
        _inst = _linker.Instantiate(_store, _module);
        _mem = _inst.GetMemory("w")!;
        
        _inst.GetAction("x")!();
    }

    string? PointerToString(IntPtr ptr, int? length)
    {
        return ptr == IntPtr.Zero ? null : length == null ? _mem.ReadNullTerminatedString(ptr) : _mem.ReadString(ptr, (int)length, Encoding.UTF8);
    }
    
    string PointerToValidString(IntPtr ptr, int? length = null)
    {
        return PointerToString(ptr, length) ?? "<null>";
    }

    public int CallMain(params string[] args)
    {
        var fArgs = new List<string> { Env["_"] };
        fArgs.AddRange(args);
        var argsSize = (fArgs.Count + 1) * 4;
        var size = argsSize + fArgs.Select(s => s.Length + 1).Sum();
        if (_mem.GetLength() < size)
            _mem.Grow((size + 0xffff) / 0xffff);
        var start = _mem.GetLength() - size;
        var cur = start;
        var stringCur = (int)cur + argsSize;
        foreach (var s in fArgs)
        {
            _mem.WriteInt32(cur, stringCur);
            Encoding.UTF8.GetBytes(s).CopyTo(_mem.GetSpan(stringCur, s.Length));
            _mem.WriteByte(stringCur + s.Length, 0);
            stringCur += s.Length + 1;
            cur += 4;
        }
        _mem.WriteInt32(cur, 0);
        return _inst.GetFunction<int, int, int>("y")!(fArgs.Count, (int)start);
    }

    public byte[] FlushStdout()
    {
        if (!Fs.IsHandleOpen(1))
            return [];
        var data = Fs.GetOpenFileStream(1)!;
        var output = data.ToArray();
        data.SetLength(0);
        //Array.Clear(data);
        return output;
    }
    
    public byte[] FlushStderr()
    {
        if (!Fs.IsHandleOpen(2))
            return [];
        var data = Fs.GetOpenFileStream(2)!;
        var output = data.ToArray();
        data.SetLength(0);
        //Array.Clear(data);
        return output;
    }

    public void FlushLogToConsole()
    {
        Console.ResetColor();
        Console.Write(Encoding.UTF8.GetString(FlushStdout()));
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write(Encoding.UTF8.GetString(FlushStderr()));
        Console.ResetColor();
    }

    public void Dispose()
    {
        Fs.Dispose();
        _inst = null!;
        _module.Dispose();
        _linker.Dispose();
        //_store.GC();
        _store.Dispose();
        _engine.Dispose();
        GC.SuppressFinalize(this);
    }
}
