using System;
using System.IO;
using System.Threading.Tasks;
using Edith.Core.Runtime;

namespace Edith.App;

public enum PermissionRisk
{
    Safe,
    Modify,
    Destructive,
    Admin
}


public sealed class PermissionManager
{
    private sealed record PendingPermission(
        Guid Id,
        string Summary,
        PermissionRisk Risk,
        DateTimeOffset CreatedUtc,
        DateTimeOffset ExpiresUtc,
        Func<Task<string>> Execute);


    private readonly object _gate =
        new();


    private PendingPermission? _pending;


    public static PermissionManager Shared { get; } =
        new();


    private PermissionManager()
    {
    }


    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                RemoveExpiredLocked();

                return
                    _pending is not null;
            }
        }
    }


    public string? PendingSummary
    {
        get
        {
            lock (_gate)
            {
                RemoveExpiredLocked();

                return
                    _pending?.Summary;
            }
        }
    }


    public PermissionRisk? PendingRisk
    {
        get
        {
            lock (_gate)
            {
                RemoveExpiredLocked();

                return
                    _pending?.Risk;
            }
        }
    }


    public string Request(
        string summary,
        PermissionRisk risk,
        Func<Task<string>> execute)
    {
        if (risk ==
            PermissionRisk.Safe)
        {
            throw new InvalidOperationException(
                "Safe action must not enter PermissionManager.");
        }


        PendingPermission pending =
            new(
                Guid.NewGuid(),
                summary,
                risk,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddSeconds(45),
                execute);


        lock (_gate)
        {
            if (_pending is not null)
            {
                Audit(
                    "REPLACED",
                    _pending);
            }


            _pending =
                pending;
        }


        Audit(
            "REQUESTED",
            pending);


        WritePendingDiagnostic(
            pending);


        return risk switch
        {
            PermissionRisk.Modify =>
                $"Нужно подтверждение: {summary}.",

            PermissionRisk.Destructive =>
                $"Внимание. Это необратимое действие: {summary}. Подтвердить?",

            PermissionRisk.Admin =>
                $"Для этого требуются повышенные права: {summary}. Подтвердить?",

            _ =>
                $"Подтвердить действие: {summary}?"
        };
    }


    public async Task<string> ConfirmAsync()
    {
        PendingPermission? pending;


        lock (_gate)
        {
            RemoveExpiredLocked();


            if (_pending is null)
            {
                return
                    "Сейчас нет действия, ожидающего подтверждения.";
            }


            pending =
                _pending;

            _pending =
                null;
        }


        Audit(
            "APPROVED",
            pending);


        try
        {
            string result =
                await pending.Execute();


            Audit(
                "EXECUTED",
                pending,
                result);


            ClearPendingDiagnostic();


            return result;
        }
        catch (Exception ex)
        {
            Audit(
                "FAILED",
                pending,
                ex.Message);


            WriteDiagnostic(
                "permission-error.txt",
                ex.ToString());


            ClearPendingDiagnostic();


            return
                "Не удалось выполнить подтверждённое действие: " +
                ex.Message;
        }
    }


    public string Deny()
    {
        PendingPermission? pending;


        lock (_gate)
        {
            RemoveExpiredLocked();


            if (_pending is null)
            {
                return
                    "Сейчас нет действия, ожидающего подтверждения.";
            }


            pending =
                _pending;

            _pending =
                null;
        }


        Audit(
            "DENIED",
            pending);


        ClearPendingDiagnostic();


        return
            "Отменено.";
    }


    private void RemoveExpiredLocked()
    {
        if (_pending is null)
        {
            return;
        }


        if (DateTimeOffset.UtcNow <=
            _pending.ExpiresUtc)
        {
            return;
        }


        Audit(
            "EXPIRED",
            _pending);


        _pending =
            null;


        ClearPendingDiagnostic();
    }


    private static void Audit(
        string status,
        PendingPermission pending,
        string? details = null)
    {
        EdithEvents.Publish(
            status switch
            {
                "REQUESTED" => EventKinds.PermissionRequested,
                "APPROVED" => EventKinds.PermissionApproved,
                "EXECUTED" => EventKinds.PermissionExecuted,
                "FAILED" => EventKinds.PermissionFailed,
                "DENIED" => EventKinds.PermissionDenied,
                "EXPIRED" or "REPLACED" => EventKinds.PermissionExpired,
                _ => "permission." + status.ToLowerInvariant()
            },
            pending.Summary,
            data: new Dictionary<string, string>
            {
                ["id"] = pending.Id.ToString(),
                ["risk"] = pending.Risk.ToString(),
                ["status"] = status,
                ["details"] = details ?? ""
            });

        try
        {
            string directory =
                @"C:\EDITH\data\audit";


            Directory.CreateDirectory(
                directory);


            string line =
                DateTimeOffset.Now.ToString("O") +
                "\t" +
                status +
                "\t" +
                pending.Risk +
                "\t" +
                pending.Id +
                "\t" +
                Sanitize(pending.Summary);


            if (!string.IsNullOrWhiteSpace(
                    details))
            {
                line +=
                    "\t" +
                    Sanitize(details);
            }


            File.AppendAllText(
                Path.Combine(
                    directory,
                    "permissions.log"),
                line +
                Environment.NewLine);
        }
        catch
        {
        }
    }


    private static string Sanitize(
        string text)
    {
        return
            text
                .Replace(
                    "\r",
                    " ")
                .Replace(
                    "\n",
                    " ")
                .Replace(
                    "\t",
                    " ")
                .Trim();
    }


    private static void WritePendingDiagnostic(
        PendingPermission pending)
    {
        WriteDiagnostic(
            "permission-pending.txt",
            $"id={pending.Id}" +
            Environment.NewLine +
            $"risk={pending.Risk}" +
            Environment.NewLine +
            $"summary={pending.Summary}" +
            Environment.NewLine +
            $"created={pending.CreatedUtc:O}" +
            Environment.NewLine +
            $"expires={pending.ExpiresUtc:O}");
    }


    private static void ClearPendingDiagnostic()
    {
        try
        {
            string file =
                @"C:\EDITH\data\diagnostics\permission-pending.txt";


            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
        }
    }


    private static void WriteDiagnostic(
        string fileName,
        string text)
    {
        try
        {
            string directory =
                @"C:\EDITH\data\diagnostics";


            Directory.CreateDirectory(
                directory);


            File.WriteAllText(
                Path.Combine(
                    directory,
                    fileName),
                text);
        }
        catch
        {
        }
    }
}
