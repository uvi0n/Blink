using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

// A managed sink is deliberately shared by both engines. Authorization, cancellation,
// accounting and socket lifetime remain under the session's control.
internal static class TransferReader {
    internal const int BufferSize = 256 * 1024;
    const uint LoadSystem32 = 0x800;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint VersionFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int ChunkFn(IntPtr context, IntPtr bytes, UIntPtr length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int StreamFn(SafeFileHandle source, ulong offset, ulong length, uint calculateCrc,
        IntPtr buffer, UIntPtr capacity,
        ChunkFn sink, IntPtr context, out uint crc);

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)]
    static extern IntPtr GetProcAddress(IntPtr handle, string name);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr handle);

    static readonly StreamFn native = Load();
    internal static bool NativeAvailable { get { return native != null; } }

    static StreamFn Load() {
        var assembly = typeof(TransferReader).Assembly;
        using(var embedded = assembly.GetManifestResourceStream("blink_core.dll")) {
            if(embedded == null)return null;
            using(var sha = SHA256.Create()) {
                string digest = BitConverter.ToString(sha.ComputeHash(embedded)).Replace("-", "");
                string appFolder = Path.GetDirectoryName(assembly.Location);
                var fn = LoadCached(embedded, sha, digest, Path.Combine(appFolder, "native-cache"));
                if(fn != null)return fn;
                string userFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if(String.IsNullOrEmpty(userFolder))return null;
                return LoadCached(embedded, sha, digest, Path.Combine(userFolder, "Blink", "native-cache"));
            }
        }
    }

    static StreamFn LoadCached(Stream embedded, HashAlgorithm sha, string digest, string folder) {
        try {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, digest + ".dll");
            if(!File.Exists(path)) {
                string temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                try {
                    using(var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        embedded.Position = 0;
                        embedded.CopyTo(output);
                    }
                    try { File.Move(temp, path); }
                    catch(IOException) { if(!File.Exists(path))throw; }
                } finally { File.Delete(temp); }
            }
            // Deny writers and renames from verification through LoadLibraryEx.
            using(var verified = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if(BitConverter.ToString(sha.ComputeHash(verified)).Replace("-", "") != digest)return null;
                return Bind(path);
            }
        } catch(IOException) { return null; }
        catch(UnauthorizedAccessException) { return null; }
        catch(System.Security.SecurityException) { return null; }
    }

    static StreamFn Bind(string path) {
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LoadSystem32);
        if(module == IntPtr.Zero)return null;
        try {
            IntPtr version = GetProcAddress(module, "blink_core_version");
            IntPtr stream = GetProcAddress(module, "blink_stream");
            if(version == IntPtr.Zero || stream == IntPtr.Zero)return null;
            if(((VersionFn)Marshal.GetDelegateForFunctionPointer(version,typeof(VersionFn)))() != 2)return null;
            var fn = (StreamFn)Marshal.GetDelegateForFunctionPointer(stream,typeof(StreamFn));
            module = IntPtr.Zero; // Keep the library mapped for the lifetime of this process.
            return fn;
        } finally {
            if(module != IntPtr.Zero)FreeLibrary(module);
        }
    }

    // False means the caller deliberately stopped after obtaining a requested ZIP range.
    // A callback failure cannot trigger fallback: the destination may already contain bytes.
    internal static bool Read(FileStream source, long offset, long length, bool calculateCrc,
        byte[] buffer, Func<byte[],int,bool> consume, out uint crc) {
        if(length == 0){crc = 0;return true;}
        if(native == null)return ReadManaged(source,offset,length,calculateCrc,buffer,consume,out crc);
        ExceptionDispatchInfo failure = null;
        int status;
        GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try {
            IntPtr address = pinned.AddrOfPinnedObject();
            ChunkFn chunk = delegate(IntPtr context, IntPtr bytes, UIntPtr size) {
                try {
                    int count = checked((int)size.ToUInt64());
                    if(bytes != address || count <= 0 || count > buffer.Length)
                        throw new InvalidDataException("Некорректный блок данных.");
                    return consume(buffer,count) ? 0 : 1;
                } catch(Exception ex) {
                    failure = ExceptionDispatchInfo.Capture(ex);
                    return -1;
                }
            };
            status = native(source.SafeFileHandle,checked((ulong)offset),checked((ulong)length),
                calculateCrc ? 1u : 0u,address,(UIntPtr)(uint)buffer.Length,chunk,IntPtr.Zero,out crc);
            GC.KeepAlive(chunk);
        } finally { pinned.Free(); }
        if(failure != null)failure.Throw();
        if(status == 0)return true;
        if(status == 1)return false;
        throw new IOException("Не удалось прочитать файл. Код Rust-ядра: "+status);
    }

    static class ManagedCrc {
        internal static readonly uint[] Table = MakeTable();
        static uint[] MakeTable() {
            var table = new uint[256];
            for(uint i=0;i<table.Length;i++){
                uint value=i;
                for(int bit=0;bit<8;bit++)value=(value&1)==0?value>>1:(value>>1)^0xedb88320U;
                table[i]=value;
            }
            return table;
        }
    }
    static bool ReadManaged(FileStream source,long offset,long remaining,bool calculateCrc,
        byte[] buffer,Func<byte[],int,bool> consume,out uint crc) {
        crc=0;uint value=uint.MaxValue;
        while(remaining>0){
            int n;
            lock(source) {
                if(source.Position!=offset)source.Position=offset;
                n=source.Read(buffer,0,(int)Math.Min(remaining,buffer.Length));
            }
            if(n==0)throw new EndOfStreamException();
            if(calculateCrc)for(int i=0;i<n;i++)value=ManagedCrc.Table[(byte)(value^buffer[i])]^(value>>8);
            if(!consume(buffer,n))return false;
            remaining-=n;offset+=n;
        }
        if(calculateCrc)crc=~value;
        return true;
    }
}
