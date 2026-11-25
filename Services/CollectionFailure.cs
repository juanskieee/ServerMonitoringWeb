using System.Management;

namespace ServerMonitoringWeb.Services;

internal static class CollectionFailure
{
    public static string Describe(
        Exception ex,
        string? password = null)
    {
        // ManagementException stores the useful WMI error code in ErrorCode.
        // Other exceptions use the regular HRESULT.
        uint code =
            ex is ManagementException managementException
                ? unchecked((uint)(int)managementException.ErrorCode)
                : unchecked((uint)ex.HResult);

        var message = GetFriendlyMessage(
            ex,
            code);

        var result =
            $"{message} (0x{code:X8})";

        return Redact(
            result,
            password);
    }

    public static string Message(
        Exception ex,
        string? password = null)
    {
        return Redact(
            ex.Message,
            password);
    }

    private static string GetFriendlyMessage(
        Exception ex,
        uint code)
    {
        return code switch
        {
            0x8007052E =>
                "Invalid username or password.",

            0x80070005 =>
                "Access denied. Check account permissions.",

            0x80041003 =>
                "WMI access denied. Check WMI/DCOM permissions.",

            0x80041064 =>
                "WMI rejected explicit credentials for a local connection. Check target address identification.",

            0x800706BA =>
                "RPC server unavailable. Check the address, firewall, and target availability.",

            0x800706BE =>
                "RPC communication failed.",

            0x80041010 =>
                "WMI class not found on target machine.",

            0x00040004 =>
                "WMI query timed out.",

            _ =>
                $"{ex.GetType().Name}: {ex.Message}"
        };
    }

    private static string Redact(
        string message,
        string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return message;
        }

        // Prevent passwords from appearing in logs,
        // warnings, exceptions, or dashboard messages.
        return message.Replace(
            password,
            "[redacted]",
            StringComparison.Ordinal);
    }
}