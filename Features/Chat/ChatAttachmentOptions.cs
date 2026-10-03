namespace Wukna.Features.Chat;

public sealed class ChatAttachmentOptions
{
    // Enable explicitly after configuring private storage and a reachable scanner.
    public bool Enabled { get; set; }
    public bool WorkerEnabled { get; set; } = true;
    public string Provider { get; set; } = "Local";
    public OciChatAttachmentOptions Oci { get; set; } = new();
    public string Directory { get; set; } = "App_Data/chat-attachments";
    public int MaxFileBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxImageBytes { get; set; } = 8 * 1024 * 1024;
    public int MaxPreviewBytes { get; set; } = 512 * 1024;
    public long BoardQuotaBytes { get; set; } = 512L * 1024 * 1024;
    public long MemberQuotaBytes { get; set; } = 128L * 1024 * 1024;
    public int MaxPendingPerBoard { get; set; } = 40;
    public int MaxPendingPerMember { get; set; } = 5;
    public int UploadsPerMinute { get; set; } = 5;
    public int IoTimeoutSeconds { get; set; } = 20;
    public int JobTimeoutSeconds { get; set; } = 60;
    public int LeaseSeconds { get; set; } = 180;
    public int ReservationSeconds { get; set; } = 300;
    public int PollMilliseconds { get; set; } = 1000;
    public int BatchSize { get; set; } = 5;
    public string ClamHost { get; set; } = "127.0.0.1";
    public int ClamPort { get; set; } = 3310;
    public int ClamTimeoutSeconds { get; set; } = 20;
}

public sealed class OciChatAttachmentOptions
{
    public string Region { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "wukna-chat";
    public string Authentication { get; set; } = "InstancePrincipals";
    public string ConfigFile { get; set; } = "";
    public string Profile { get; set; } = "DEFAULT";

    public bool IsValid() => IsName(Region, 80) && IsName(Namespace, 100) && IsName(Bucket, 100) &&
        IsName(Prefix, 100) && (Authentication == "InstancePrincipals" ||
            Authentication == "ConfigFile" && Path.IsPathFullyQualified(ConfigFile) && IsName(Profile, 100));

    // A single prefix segment prevents ambiguous ownership and accidental cross-prefix cleanup.
    private static bool IsName(string value, int limit) => value.Length is > 0 && value.Length <= limit &&
        value.AsSpan().IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.") < 0 &&
        value is not ("." or "..");
}
