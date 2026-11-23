using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace KillerPDF.Services;

internal sealed class SingleInstanceChannel(string applicationName, string accountId, int sessionId)
{
    /// <summary>The longest forwarded message: a path or URL plus its newline.</summary>
    internal const int MaximumMessageBytes = 32 * 1024;
    /// <summary>How long a connected sender has to deliver its whole message.</summary>
    internal static readonly TimeSpan MessageDeadline = TimeSpan.FromSeconds(2);

    private readonly string scope = accountId + "." + sessionId.ToString(CultureInfo.InvariantCulture);
    public string MutexName => @"Local\" + applicationName + ".SingleInstance." + scope;
    public string PipeName => applicationName + ".OpenPipe." + scope;

    /// <summary>
    /// The pipe only this user account may open (SYSTEM keeps its usual access), so a forwarded
    /// open can come from this user's own launches and nothing else.
    /// </summary>
    public NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        // The owner is set to the user even when elevated, where Windows would make it
        // Administrators, so a sender's owner check below matches either way.
        security.SetOwner(new SecurityIdentifier(accountId));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(accountId),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>
    /// Reads one newline-terminated UTF-8 message, or returns null when the sender stalls past
    /// <paramref name="deadline"/>, sends more than <paramref name="maximumBytes"/>, or disconnects
    /// before a newline. A silent or flooding client can no longer hold the only pipe instance.
    /// </summary>
    internal static string? ReadMessage(Stream stream, int maximumBytes, TimeSpan deadline)
    {
        using var cancel = new CancellationTokenSource(deadline);
        var received = new MemoryStream();
        byte[] buffer = new byte[1024];
        try
        {
            while (true)
            {
                int read = stream.ReadAsync(buffer, cancel.Token).AsTask().GetAwaiter().GetResult();
                if (read == 0) return null;
                int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                int take = newline >= 0 ? newline : read;
                if (received.Length + take > maximumBytes) return null;
                received.Write(buffer, 0, take);
                if (newline >= 0)
                    return Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length).TrimEnd('\r');
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (IOException) { return null; }
    }

    internal static bool IsOwnedBy(SecurityIdentifier? owner, SecurityIdentifier? user) =>
        owner is not null && user is not null && owner == user;

    public int ForwardLaunch(string? path, int timeoutMilliseconds = 3000)
    {
        return TrySend(path, timeoutMilliseconds) ? 0 : 1;
    }

    public bool TrySend(string? path, int timeoutMilliseconds = 3000)
    {
        try
        {
            // Only a pipe this user owns gets the path, so another account that made this name first
            // cannot collect the paths and links this user opens. .NET's CurrentUserOnly compares the
            // owner with the token's Owner, which is Administrators when elevated, so an elevated
            // sender refused our own pipe; the user SID is the owner CreateServer always sets.
            using var client = new NamedPipeClientStream(".", PipeName,
                PipeAccessRights.WriteData | PipeAccessRights.ReadPermissions, PipeOptions.None,
                TokenImpersonationLevel.None, HandleInheritability.None);
            client.Connect(timeoutMilliseconds);
            using (var identity = WindowsIdentity.GetCurrent())
                if (!IsOwnedBy(client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier, identity.User))
                    return false;
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(path ?? "");
            return true;
        }
        catch { return false; }
    }
}
