using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

internal static class ArtifactReceipt
{
    // Serialise receipt updates shared by simultaneous local tool calls.
    internal static IDisposable Lock(string path, CancellationToken cancellationToken = default)
    {
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        var mutex=new Mutex(false,"TransketchSite-"+key);
        try
        {
            if (WaitHandle.WaitAny([cancellationToken.WaitHandle, mutex]) == 0)
                throw new OperationCanceledException(cancellationToken);
        }
        catch (AbandonedMutexException) { }
        catch { mutex.Dispose(); throw; }
        return new Lease(mutex);
    }
    private sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    internal static string Digest(string path, CancellationToken cancellationToken = default)
    {
        using var file=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashDataAsync(file, cancellationToken).AsTask().GetAwaiter().GetResult());
    }
    internal static bool Matches(string path,string signature)
    {
        if (!File.Exists(path) || !File.Exists(path+".receipt")) return false;
        return File.ReadAllText(path+".receipt")==Seal(path,signature);
    }
    internal static void Save(string path,string signature)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(temp,Seal(path,signature)); File.Move(temp,path+".receipt",true); }
        finally { File.Delete(temp); }
    }
    private static string Seal(string path,string signature) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature+"\n"+Digest(path))));
}
